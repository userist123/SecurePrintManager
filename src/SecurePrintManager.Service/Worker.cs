using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecurePrintManager.Core;
using SecurePrintManager.Database;
using SecurePrintManager.Service.Ipc;

namespace SecurePrintManager.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private PrintMonitor? _printMonitor;
    private PdfSpoolWatcher? _pdfWatcher;
    private ScanSpoolWatcher? _scanWatcher;
    private Task? _pipeServerTask;

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
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<FileEncryptionService>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLogger>();
            var quotaManager = scope.ServiceProvider.GetRequiredService<QuotaManager>();
            var pdfWatcher = scope.ServiceProvider.GetRequiredService<PdfSpoolWatcher>();
            var scanWatcher = scope.ServiceProvider.GetRequiredService<ScanSpoolWatcher>();

            await db.Database.EnsureCreatedAsync(stoppingToken);

            // Get spool folders from config
            var pdfFolderConfig = db.Configs.FirstOrDefault(c => c.Key == "PdfSpoolFolder");
            var scanFolderConfig = db.Configs.FirstOrDefault(c => c.Key == "ScanSpoolFolder");

            var pdfFolder = pdfFolderConfig?.Value ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SecurePrintManager",
                "PdfSpool"
            );

            var scanFolder = scanFolderConfig?.Value ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SecurePrintManager",
                "ScanSpool"
            );

            // Ensure folders exist
            Directory.CreateDirectory(pdfFolder);
            Directory.CreateDirectory(scanFolder);

            // Start watchers
            _printMonitor = new PrintMonitor(db, encryption, audit);
            _pdfWatcher = pdfWatcher;
            _scanWatcher = scanWatcher;

            _pdfWatcher.Start(pdfFolder);
            _scanWatcher.Start(scanFolder);

            // Pipe server-ul de release: construit manual cu instanțele din acest scope,
            // la fel ca PrintMonitor mai sus - vezi nota din ServiceCollectionExtensions
            // despre lifetime-ul serviciilor (DatabaseContext e Scoped dar consumat ca și
            // cum ar fi Singleton pe durata worker-ului; nu schimb asta acum, doar rămân
            // consistent cu tiparul existent în loc să introduc un al treilea).
            var pipeLogger = scope.ServiceProvider.GetRequiredService<ILogger<PrintManagerPipeServer>>();
            var pipeServer = new PrintManagerPipeServer(pipeLogger, db, audit, quotaManager);
            _pipeServerTask = pipeServer.RunAsync(stoppingToken);

            _logger.LogInformation("Watchers started. PDF: {PdfFolder}, Scan: {ScanFolder}", pdfFolder, scanFolder);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
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
            _pdfWatcher?.Stop();
            _scanWatcher?.Stop();

            if (_pipeServerTask != null)
            {
                try
                {
                    // RunAsync se oprește singur pe OperationCanceledException când
                    // stoppingToken e semnalat; dăm doar un răgaz scurt să se termine curat.
                    await Task.WhenAny(_pipeServerTask, Task.Delay(TimeSpan.FromSeconds(3)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PrintManagerPipeServer nu s-a oprit curat");
                }
            }

            _logger.LogInformation("SecurePrintManager Worker stopped at {Time}", DateTimeOffset.Now);
        }
    }

    private async Task CleanupExpiredJobsAsync(DatabaseContext db, AuditLogger audit, CancellationToken cancellationToken)
    {
        try
        {
            var config = db.Configs.FirstOrDefault(c => c.Key == "JobTimeoutHours");
            if (config == null) return;

            if (!int.TryParse(config.Value, out var timeoutHours)) return;

            var expiryThreshold = DateTime.Now.AddHours(-timeoutHours);

            var expiredJobs = db.PrintJobs
                .Where(j => j.Status == "HOLD" && j.Timestamp < expiryThreshold)
                .ToList();

            foreach (var job in expiredJobs)
            {
                job.Status = "EXPIRED";
                _logger.LogInformation("Job {JobId} expired", job.Id);
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
