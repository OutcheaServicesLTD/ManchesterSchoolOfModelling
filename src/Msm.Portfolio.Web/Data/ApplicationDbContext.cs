using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Data;

/// <summary>
/// Application data context. Deliberately free of provider-specific constructs so the
/// database decision stays open (specification section 32).
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<ClientProfile> ClientProfiles => Set<ClientProfile>();
    public DbSet<ModelMeasurement> ModelMeasurements => Set<ModelMeasurement>();
    public DbSet<GuardianConsent> GuardianConsents => Set<GuardianConsent>();
    public DbSet<Domain.Entities.Portfolio> Portfolios => Set<Domain.Entities.Portfolio>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<RetoucherAssignment> RetoucherAssignments => Set<RetoucherAssignment>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<MaintenanceSubscription> MaintenanceSubscriptions => Set<MaintenanceSubscription>();
    public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // SQL Server treats NULLs as equal in a unique index, so a plain unique index on
        // a nullable column would permit only one NULL row. SQLite and PostgreSQL treat
        // NULLs as distinct and need no filter. This is the one place the model is aware
        // of the provider, and it exists to keep behaviour identical across all three.
        var nullFilter = Database.IsSqlServer()
            ? (Func<string, string?>)(column => $"[{column}] IS NOT NULL")
            : _ => null;

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.FirstName).HasMaxLength(100);
            entity.Property(u => u.LastName).HasMaxLength(100);
        });

        builder.Entity<ApplicationRole>(entity =>
        {
            entity.Property(r => r.Description).HasMaxLength(256);
        });

        builder.Entity<ClientProfile>(entity =>
        {
            entity.Property(c => c.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(c => c.LastName).HasMaxLength(100).IsRequired();
            entity.Property(c => c.DisplayName).HasMaxLength(150);
            entity.Property(c => c.Location).HasMaxLength(200);
            entity.Property(c => c.HairColour).HasMaxLength(50);
            entity.Property(c => c.EyeColour).HasMaxLength(50);
            entity.Property(c => c.InstagramUrl).HasMaxLength(500);
            entity.Property(c => c.TikTokUrl).HasMaxLength(500);
            entity.Property(c => c.GhlContactId).HasMaxLength(100);

            entity.HasOne(c => c.ApplicationUser)
                .WithOne(u => u.ClientProfile)
                .HasForeignKey<ClientProfile>(c => c.ApplicationUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(c => c.ApplicationUserId).IsUnique();

            // One CRM contact maps to one client. Clients created before their CRM link
            // exists carry a null and must not collide with each other.
            entity.HasIndex(c => c.GhlContactId)
                .IsUnique()
                .HasFilter(nullFilter(nameof(ClientProfile.GhlContactId)));
        });

        builder.Entity<ModelMeasurement>(entity =>
        {
            entity.Property(m => m.MeasurementType).HasMaxLength(100).IsRequired();
            entity.Property(m => m.Value).HasMaxLength(100).IsRequired();
            entity.Property(m => m.CanonicalValue).HasPrecision(10, 2);

            entity.HasOne(m => m.Client)
                .WithMany(c => c.Measurements)
                .HasForeignKey(m => m.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(m => new { m.ClientId, m.MeasurementType }).IsUnique();
        });

        builder.Entity<GuardianConsent>(entity =>
        {
            entity.Property(g => g.GuardianName).HasMaxLength(200).IsRequired();
            entity.Property(g => g.Relationship).HasMaxLength(100).IsRequired();
            entity.Property(g => g.Email).HasMaxLength(256).IsRequired();
            entity.Property(g => g.Phone).HasMaxLength(50);
            entity.Property(g => g.ConsentVersion).HasMaxLength(50);
            entity.Property(g => g.VerificationToken).HasMaxLength(128).IsRequired();

            entity.HasOne(g => g.Client)
                .WithOne(c => c.GuardianConsent)
                .HasForeignKey<GuardianConsent>(g => g.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(g => g.VerificationToken).IsUnique();
        });

        builder.Entity<Domain.Entities.Portfolio>(entity =>
        {
            entity.Property(p => p.Slug).HasMaxLength(160);
            entity.Property(p => p.CrmSyncError).HasMaxLength(1000);

            entity.HasOne(p => p.Client)
                .WithOne(c => c.Portfolio)
                .HasForeignKey<Domain.Entities.Portfolio>(p => p.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // The featured image must not disappear from under a live portfolio, so
            // deleting the asset is restricted rather than cascading.
            entity.HasOne(p => p.FeaturedMedia)
                .WithMany()
                .HasForeignKey(p => p.FeaturedMediaId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(p => p.ClientId).IsUnique();

            // Slugs are the public URL, so they must be unique. Portfolios before first
            // publication have no slug yet and must not collide with each other.
            entity.HasIndex(p => p.Slug)
                .IsUnique()
                .HasFilter(nullFilter(nameof(Domain.Entities.Portfolio.Slug)));

            entity.HasIndex(p => p.Status);
        });

        builder.Entity<MediaAsset>(entity =>
        {
            entity.Property(m => m.StorageKey).HasMaxLength(500).IsRequired();
            entity.Property(m => m.OriginalFilename).HasMaxLength(300).IsRequired();
            entity.Property(m => m.MimeType).HasMaxLength(120).IsRequired();

            entity.HasOne(m => m.Client)
                .WithMany(c => c.MediaAssets)
                .HasForeignKey(m => m.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Keep the upload attributable even if the staff account is later removed.
            entity.HasOne(m => m.UploadedByUser)
                .WithMany()
                .HasForeignKey(m => m.UploadedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(m => new { m.ClientId, m.IsSelectedForPortfolio });
            entity.HasIndex(m => new { m.ClientId, m.DisplayOrder });
        });

        builder.Entity<RetoucherAssignment>(entity =>
        {
            entity.HasOne(a => a.Client)
                .WithMany(c => c.RetoucherAssignments)
                .HasForeignKey(a => a.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.RetoucherUser)
                .WithMany()
                .HasForeignKey(a => a.RetoucherUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(a => new { a.RetoucherUserId, a.Status });
        });

        builder.Entity<Product>(entity =>
        {
            entity.Property(p => p.Code).HasMaxLength(100).IsRequired();
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();
            entity.Property(p => p.Description).HasMaxLength(1000);
            entity.Property(p => p.Price).HasPrecision(18, 2);
            entity.Property(p => p.Currency).HasMaxLength(3).IsRequired();

            entity.HasIndex(p => p.Code).IsUnique();
        });

        builder.Entity<Order>(entity =>
        {
            entity.Property(o => o.Amount).HasPrecision(18, 2);
            entity.Property(o => o.Currency).HasMaxLength(3).IsRequired();
            entity.Property(o => o.GoCardlessReference).HasMaxLength(200);

            entity.HasOne(o => o.Client)
                .WithMany(c => c.Orders)
                .HasForeignKey(o => o.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

            // Products are never removed once sold, so order history stays intact.
            entity.HasOne(o => o.Product)
                .WithMany()
                .HasForeignKey(o => o.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(o => o.GoCardlessReference);
            entity.HasIndex(o => new { o.ClientId, o.Status });
        });

        builder.Entity<PaymentTransaction>(entity =>
        {
            entity.Property(t => t.Provider).HasMaxLength(50).IsRequired();
            entity.Property(t => t.ProviderPaymentId).HasMaxLength(200);
            entity.Property(t => t.Amount).HasPrecision(18, 2);
            entity.Property(t => t.Currency).HasMaxLength(3).IsRequired();
            entity.Property(t => t.FailureReason).HasMaxLength(1000);

            entity.HasOne(t => t.Order)
                .WithMany(o => o.Transactions)
                .HasForeignKey(t => t.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(t => t.ProviderPaymentId);
        });

        builder.Entity<MaintenanceSubscription>(entity =>
        {
            entity.Property(s => s.ProviderSubscriptionId).HasMaxLength(200);
            entity.Property(s => s.PriceAtCreation).HasPrecision(18, 2);
            entity.Property(s => s.Currency).HasMaxLength(3).IsRequired();

            entity.HasOne(s => s.Client)
                .WithMany()
                .HasForeignKey(s => s.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.Product)
                .WithMany()
                .HasForeignKey(s => s.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(s => s.ProviderSubscriptionId);
            entity.HasIndex(s => s.Status);
        });

        builder.Entity<PaymentWebhookEvent>(entity =>
        {
            entity.Property(e => e.Provider).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProviderEventId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ProcessingError).HasMaxLength(2000);

            // This is what makes webhook handling idempotent: a replayed event cannot
            // be stored, and therefore cannot be applied, twice (specification section 44).
            entity.HasIndex(e => new { e.Provider, e.ProviderEventId }).IsUnique();
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
            entity.Property(a => a.EntityId).HasMaxLength(100).IsRequired();
            entity.Property(a => a.Action).HasMaxLength(100).IsRequired();

            // Audit history outlives the account that produced it.
            entity.HasOne(a => a.User)
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(a => new { a.EntityType, a.EntityId });
            entity.HasIndex(a => a.Timestamp);
        });

        builder.Entity<Notification>(entity =>
        {
            entity.Property(n => n.Type).HasMaxLength(100).IsRequired();
            entity.Property(n => n.Message).HasMaxLength(1000).IsRequired();
            entity.Property(n => n.Url).HasMaxLength(500);

            entity.HasOne(n => n.User)
                .WithMany()
                .HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(n => new { n.UserId, n.IsRead });
        });

        builder.Entity<SystemSetting>(entity =>
        {
            entity.Property(s => s.Key).HasMaxLength(100).IsRequired();
            entity.Property(s => s.Value).HasMaxLength(2000);
            entity.Property(s => s.Description).HasMaxLength(500);

            entity.HasIndex(s => s.Key).IsUnique();
        });
    }
}
