using Microsoft.EntityFrameworkCore;
using SecurePrintManager.Core;

namespace SecurePrintManager.Database;

public class DatabaseContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<PrintJob> PrintJobs { get; set; }
    public DbSet<ScanJob> ScanJobs { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<Config> Configs { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SecurePrintManager",
            "secureprint.db"
        );

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        optionsBuilder.UseSqlite($"Data Source={dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PinCode);
            entity.HasIndex(e => e.CardCode);
            entity.HasIndex(e => e.IsActive);
            entity.Property(e => e.Username).IsRequired();
        });

        modelBuilder.Entity<PrintJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Timestamp);
            entity.HasOne<User>().WithMany().HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<ScanJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasOne<User>().WithMany().HasForeignKey(e => e.UserId);
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

        // Seed config default
        modelBuilder.Entity<Config>().HasData(
            new Config { Key = "JobTimeoutHours", Value = "24", UpdatedAt = DateTime.Now },
            new Config { Key = "SessionTimeoutMinutes", Value = "15", UpdatedAt = DateTime.Now },
            new Config { Key = "CostPerPageBW", Value = "0.10", UpdatedAt = DateTime.Now },
            new Config { Key = "CostPerPageColor", Value = "0.50", UpdatedAt = DateTime.Now },
            new Config { Key = "CostPerScan", Value = "0.05", UpdatedAt = DateTime.Now },
            new Config { Key = "EnableEncryption", Value = "true", UpdatedAt = DateTime.Now },
            new Config { Key = "EnableAuditLog", Value = "true", UpdatedAt = DateTime.Now }
        );
    }
}
