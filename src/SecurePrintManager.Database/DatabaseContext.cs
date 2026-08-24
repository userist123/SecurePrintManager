using Microsoft.EntityFrameworkCore;

namespace SecurePrintManager.Database;

public class DatabaseContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<PrintJob> PrintJobs { get; set; }
    public DbSet<ScanJob> ScanJobs { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<Config> Configs { get; set; }

    public DatabaseContext(DbContextOptions<DatabaseContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.HasIndex(e => e.IsActive);
            entity.Property(e => e.Username).IsRequired();
        });

        modelBuilder.Entity<PrintJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Timestamp);
            entity.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ScanJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.Action);
            entity.HasIndex(e => e.Username);
        });

        modelBuilder.Entity<Config>(entity =>
        {
            entity.HasKey(e => e.Key);
        });

        modelBuilder.Entity<Config>().HasData(
            new Config { Key = "JobTimeoutHours", Value = "24", UpdatedAt = new DateTime(2026, 1, 1) },
            new Config { Key = "SessionTimeoutMinutes", Value = "15", UpdatedAt = new DateTime(2026, 1, 1) },
            new Config { Key = "CostPerPageBW", Value = "0.10", UpdatedAt = new DateTime(2026, 1, 1) },
            new Config { Key = "CostPerPageColor", Value = "0.50", UpdatedAt = new DateTime(2026, 1, 1) },
            new Config { Key = "CostPerScan", Value = "0.05", UpdatedAt = new DateTime(2026, 1, 1) },
            new Config { Key = "EnableEncryption", Value = "true", UpdatedAt = new DateTime(2026, 1, 1) },
            new Config { Key = "EnableAuditLog", Value = "true", UpdatedAt = new DateTime(2026, 1, 1) }
        );
    }
}
