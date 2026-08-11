using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecurePrintManager.Core;
using SecurePrintManager.Database;

namespace SecurePrintManager.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private PrintMonitor? _printMonitor;

    public Worker(ILogger<Worker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SecurePrintManager Worker starting at {Time}", DateTimeOffset.Now);

        try
        {
            // Create scope to resolve scoped services
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<FileEncryptionService>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLogger>();

            // Ensure database exists
            await db.Database.EnsureCreatedAsync(stoppingToken);
            _logger.LogInformation("Database initialized at {Path}", GetDatabasePath());

            // Initialize PrintMonitor
            _printMonitor = new PrintMonitor(db, encryption, audit);
            _logger.LogInformation("PrintMonitor initialized and listening for print jobs");

            // Run until shutdown
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                
                // Optional: periodic cleanup of expired jobs
                await CleanupExpiredJobsAsync(db, audit, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker cancellation requested");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker encountered an error");
            throw;
        }
        finally
        {
            _printMonitor?.Dispose();
            _logger.LogInformation("SecurePrintManager Worker stopped at {Time}", DateTimeOffset.Now);
        }
    }

    private string GetDatabasePath()
    {
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SecurePrintManager",
            "secureprint.db"
        );
    }

    private async Task CleanupExpiredJobsAsync(DatabaseContext db, AuditLogger audit, CancellationToken cancellationToken)
    {
        try
        {
            // Get timeout from config
            var config = db.Configs.FirstOrDefault(c => c.Key == "JobTimeoutHours");
            if (config == null) return;

            if (!int.TryParse(config.Value, out var timeoutHours)) return;

            var expiryThreshold = DateTime.Now.AddHours(-timeoutHours);

            // Find expired HOLD jobs
            var expiredJobs = db.PrintJobs
                .Where(j => j.Status == "HOLD" && j.Timestamp < expiryThreshold)
                .ToList();

            foreach (var job in expiredJobs)
            {
                job.Status = "EXPIRED";
                _logger.LogInformation("Job {JobId} expired and marked for deletion", job.Id);
                audit.Log("EXPIRE", "SYSTEM", job.DocumentName, $"Job {job.Id} expired after {timeoutHours} hours");
            }

            if (expiredJobs.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleaned up {Count} expired jobs", expiredJobs.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during job cleanup");
        }
    }
}
