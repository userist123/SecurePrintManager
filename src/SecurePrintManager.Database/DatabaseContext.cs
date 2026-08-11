using Microsoft.EntityFrameworkCore;
namespace SecurePrintManager.Database;
public sealed class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<PrintJobEntity> PrintJobs => Set<PrintJobEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserEntity>().HasKey(x=>x.Id); b.Entity<UserEntity>().HasIndex(x=>x.UserName).IsUnique();
        b.Entity<PrintJobEntity>().HasKey(x=>x.Id); b.Entity<AuditLogEntity>().HasKey(x=>x.Id);
    }
}
public sealed class UserEntity { public Guid Id {get;set;} public required string UserName {get;set;} public bool Enabled {get;set;}=true; public int PageQuota {get;set;} }
public sealed class PrintJobEntity { public Guid Id {get;set;} public Guid UserId {get;set;} public required string PrinterName {get;set;} public required string DocumentName {get;set;} public int Pages {get;set;} public string State {get;set;}="Created"; public DateTimeOffset CreatedAt {get;set;} }
public sealed class AuditLogEntity { public Guid Id {get;set;} public DateTimeOffset Timestamp {get;set;} public required string Actor {get;set;} public required string Action {get;set;} public required string Resource {get;set;} public required string Result {get;set;} public required string PreviousHash {get;set;} public required string Hash {get;set;} }
