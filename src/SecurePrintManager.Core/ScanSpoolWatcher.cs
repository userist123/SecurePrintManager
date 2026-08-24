using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SecurePrintManager.Database;

namespace SecurePrintManager.Core;

/// <summary>
/// Monitorizează un folder pentru fișiere PDF/Imagine nou create (scan to folder).
/// Fiecare fișier devine un ScanJob.
/// </summary>
public class ScanSpoolWatcher : IDisposable
{
    private readonly DatabaseContext _db;
    private readonly AuditLogger _audit;
    private readonly QuotaManager _quotaManager;
    private readonly ILogger<ScanSpoolWatcher>? _logger;
    private FileSystemWatcher? _watcher;
    private string _scanFolder = string.Empty;
    private bool _isRunning;

    public ScanSpoolWatcher(DatabaseContext db, AuditLogger audit, QuotaManager quotaManager, ILogger<ScanSpoolWatcher>? logger = null)
    {
        _db = db;
        _audit = audit;
        _quotaManager = quotaManager;
        _logger = logger;
    }

    public void Start(string scanFolder)
    {
        if (_isRunning)
        {
            _logger?.LogWarning("ScanSpoolWatcher is already running");
            return;
        }

        if (!Directory.Exists(scanFolder))
        {
            Directory.CreateDirectory(scanFolder);
        }

        _scanFolder = scanFolder;
        _isRunning = true;

        _watcher = new FileSystemWatcher(scanFolder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = true
        };

        _watcher.Created += OnScanCreated;
        _logger?.LogInformation("ScanSpoolWatcher started on {Folder}", scanFolder);
    }

    public void Stop()
    {
        if (!_isRunning || _watcher == null) return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnScanCreated;
        _watcher.Dispose();
        _watcher = null;
        _isRunning = false;
        _logger?.LogInformation("ScanSpoolWatcher stopped");
    }

    private void OnScanCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Așteptăm să se finalizeze scrierea
            System.Threading.Thread.Sleep(500);

            var filePath = e.FullPath;
            if (!File.Exists(filePath)) return;

            var extension = Path.GetExtension(e.Name ?? "").ToLower();
            if (extension is not (".pdf" or ".png" or ".jpg" or ".jpeg" or ".tiff"))
            {
                return; // Ignorăm fișierele non-scan
            }

            var fileName = Path.GetFileNameWithoutExtension(e.Name ?? "Unknown");
            var fileSize = new FileInfo(filePath).Length;

            // Estimăm pagini (1 pagină per fișier, sau poți analiza PDF multi-pagină)
            var pages = 1;

            // Identificăm userul (mapping folder→user sau user generic)
            var defaultUser = _db.Users.FirstOrDefault(u => u.IsActive) ?? null;
            if (defaultUser == null)
            {
                _logger?.LogWarning("No active user found for scan {File}", filePath);
                return;
            }

            // Verificăm quota de scanări
            if (!_quotaManager.CheckQuota(defaultUser.Id, 0))
            {
                _audit.Log("BLOCK", defaultUser.Username, "Scan", "Scan quota exceeded");
                return;
            }

            // Calculăm costul
            var costConfig = _db.Configs.FirstOrDefault(c => c.Key == "CostPerScan");
            var costPerScan = decimal.Parse(costConfig?.Value ?? "0.05");

            // Creăm ScanJob
            var scanJob = new ScanJob
            {
                UserId = defaultUser.Id,
                Pages = pages,
                Destination = extension == ".pdf" ? "PDF" : "Image",
                FilePath = filePath,
                Cost = costPerScan,
                Timestamp = DateTime.Now
            };

            _db.ScanJobs.Add(scanJob);

            // Actualizăm quota
            defaultUser.ScansUsed += 1;
            _db.SaveChanges();

            _audit.Log("SCAN", defaultUser.Username, $"Scan-{scanJob.Id}", $"File: {fileName}, Type: {extension}, Cost: {costPerScan}");
            _logger?.LogInformation("Scan job {JobId} created for {File}", scanJob.Id, filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing scan {File}", e.FullPath);
            _audit.Log("ERROR", "SYSTEM", "ScanSpoolWatcher", ex.Message);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
