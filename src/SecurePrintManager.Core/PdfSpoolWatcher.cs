using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SecurePrintManager.Database;
using SecurePrintManager.Database.Models;

namespace SecurePrintManager.Core;

/// <summary>
/// Monitorizează un folder pentru fișiere PDF nou create (print to PDF).
/// Fiecare PDF devine un PrintJob în HOLD.
/// </summary>
public class PdfSpoolWatcher : IDisposable
{
    private readonly DatabaseContext _db;
    private readonly AuditLogger _audit;
    private readonly ILogger<PdfSpoolWatcher>? _logger;
    private FileSystemWatcher? _watcher;
    private string _pdfFolder = string.Empty;
    private bool _isRunning;

    public PdfSpoolWatcher(DatabaseContext db, AuditLogger audit, ILogger<PdfSpoolWatcher>? logger = null)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public void Start(string pdfFolder)
    {
        if (_isRunning)
        {
            _logger?.LogWarning("PdfSpoolWatcher is already running");
            return;
        }

        if (!Directory.Exists(pdfFolder))
        {
            Directory.CreateDirectory(pdfFolder);
        }

        _pdfFolder = pdfFolder;
        _isRunning = true;

        _watcher = new FileSystemWatcher(pdfFolder, "*.pdf")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnPdfCreated;
        _logger?.LogInformation("PdfSpoolWatcher started on {Folder}", pdfFolder);
    }

    public void Stop()
    {
        if (!_isRunning || _watcher == null) return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnPdfCreated;
        _watcher.Dispose();
        _watcher = null;
        _isRunning = false;
        _logger?.LogInformation("PdfSpoolWatcher stopped");
    }

    private void OnPdfCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Așteptăm puțin să se finalizeze scrierea fișierului
            System.Threading.Thread.Sleep(500);

            var filePath = e.FullPath;
            if (!File.Exists(filePath)) return;

            // Extragem numele fișierului (fără extensie) ca document name
            var fileName = Path.GetFileNameWithoutExtension(e.Name ?? "Unknown");
            var fileSize = new FileInfo(filePath).Length;

            // Estimăm numărul de pagini (simplist: 1 pagină per ~50KB PDF)
            var estimatedPages = Math.Max(1, (int)(fileSize / 50000));

            // Identificăm userul (poți extinde: mapping folder→user, sau metadata din PDF)
            // Pentru moment, folosim un user generic sau primul user activ
            var defaultUser = _db.Users.FirstOrDefault(u => u.IsActive) ?? null;
            if (defaultUser == null)
            {
                _logger?.LogWarning("No active user found for PDF {File}", filePath);
                return;
            }

            // Verificăm dacă job-ul există deja (evităm duplicate)
            var existingJob = _db.PrintJobs
                .FirstOrDefault(j => j.SpoolFile == filePath);

            if (existingJob != null)
            {
                _logger?.LogDebug("Job already exists for {File}", filePath);
                return;
            }

            // Calculăm costul
            var costConfig = _db.Configs.FirstOrDefault(c => c.Key == "CostPerPageBW");
            var costPerPage = decimal.Parse(costConfig?.Value ?? "0.10");
            var totalCost = costPerPage * estimatedPages;

            // Creăm PrintJob
            var printJob = new PrintJob
            {
                UserId = defaultUser.Id,
                DocumentName = fileName,
                Pages = estimatedPages,
                PrinterName = "PDF Port",
                SpoolFile = filePath,
                SpoolFileHash = ComputeHash(filePath),
                Color = false,
                Duplex = false,
                Status = "HOLD",
                Timestamp = DateTime.Now,
                Cost = totalCost
            };

            _db.PrintJobs.Add(printJob);
            _db.SaveChanges();

            _audit.Log("PRINT", defaultUser.Username, fileName, $"PDF captured. Pages: {estimatedPages}, Cost: {totalCost}");
            _logger?.LogInformation("PDF job {JobId} created for {File}", printJob.Id, filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing PDF {File}", e.FullPath);
            _audit.Log("ERROR", "SYSTEM", "PdfSpoolWatcher", ex.Message);
        }
    }

    private string ComputeHash(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    public void Dispose()
    {
        Stop();
    }
}
