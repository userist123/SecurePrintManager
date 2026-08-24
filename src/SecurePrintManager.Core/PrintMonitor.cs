using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using Microsoft.Win32;
using SecurePrintManager.Database;

namespace SecurePrintManager.Core;

/// <summary>
/// Monitorizează job-urile de printare la nivel de sistem via WMI (Win32_PrintJob).
///
/// De ce nu System.Printing (PrintQueue): documentația Microsoft afirmă explicit că
/// "Classes within the System.Printing namespace are not supported for use within a
/// Windows service or ASP.NET application or service" — vezi
/// https://learn.microsoft.com/en-us/dotnet/api/system.printing.printsystemjobinfo
/// În plus, PrintQueue nu expune niciun eveniment "JobAdded" în .NET — API-ul din
/// implementarea anterioară nu exista și nu s-ar fi compilat.
///
/// Detaliile precise (pagini, culoare, duplex) și controlul (pauză/reluare/anulare), pe
/// care WMI nu le oferă fiabil, vin din SpoolerControl (winspool.drv interop).
/// </summary>
public class PrintMonitor : IDisposable
{
    private readonly DatabaseContext _db;
    private readonly FileEncryptionService _encryption;
    private readonly AuditLogger _audit;
    private ManagementEventWatcher? _watcher;

    public PrintMonitor(DatabaseContext db, FileEncryptionService encryption, AuditLogger audit)
    {
        _db = db;
        _encryption = encryption;
        _audit = audit;

        var query = new WqlEventQuery(
            "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PrintJob'");

        _watcher = new ManagementEventWatcher(query);
        _watcher.EventArrived += OnJobAdded;
        _watcher.Start();
    }

    private void OnJobAdded(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var job = (ManagementBaseObject)e.NewEvent["TargetInstance"];

            // Win32_PrintJob.Name are forma "PrinterName,JobID"
            var name = job["Name"] as string ?? string.Empty;
            var printerName = name.Split(',').FirstOrDefault()?.Trim() ?? string.Empty;
            var jobId = Convert.ToInt32(job["JobId"]);
            var owner = job["Owner"] as string ?? "UNKNOWN";
            var docName = job["Document"] as string ?? "(fara nume)";

            if (string.IsNullOrEmpty(printerName))
            {
                _audit.Log("ERROR", "SYSTEM", docName, $"Nu s-a putut determina printerul pentru job {jobId}");
                return;
            }

            var dbUser = _db.Users.FirstOrDefault(u => u.Username == owner);
            if (dbUser == null || !dbUser.IsActive)
            {
                _audit.Log("BLOCK", owner, docName, "Utilizator inexistent sau inactiv");
                TryCancel(printerName, jobId, owner, docName);
                return;
            }

            int pages;
            bool isColor, isDuplex;
            if (!SpoolerControl.TryGetJobDetails(printerName, jobId, out pages, out isColor, out isDuplex))
            {
                pages = job["TotalPages"] is uint tp && tp > 0 ? (int)tp : 1;
                isColor = false;
                isDuplex = false;
            }

            if (dbUser.PagesUsed + pages > dbUser.MonthlyQuota)
            {
                _audit.Log("BLOCK", owner, docName, $"Quota depasita ({dbUser.PagesUsed}+{pages}>{dbUser.MonthlyQuota})");
                TryCancel(printerName, jobId, owner, docName);
                return;
            }

            // Pauzează job-ul la nivel de spooler înainte de a ajunge la imprimantă.
            // Dacă asta eșuează, jobul continuă spre imprimantă fără control - nu are rost
            // să-l mai ținem "HOLD" în DB, deci logăm eroarea și ieșim fără a-l insera.
            try
            {
                SpoolerControl.Pause(printerName, jobId);
            }
            catch (Exception ex)
            {
                _audit.Log("ERROR", owner, docName, $"Nu s-a putut pauza job {jobId}: {ex.Message}. Jobul va ajunge netinut la imprimanta.");
                return;
            }

            var spoolPath = TryCopySpoolFile(printerName, jobId);
            var spoolHash = spoolPath != null ? ComputeHash(spoolPath) : null;

            var printJob = new PrintJob
            {
                UserId = dbUser.Id,
                DocumentName = docName,
                Pages = pages,
                PrinterName = printerName,
                SpoolerJobId = jobId,
                SpoolFile = spoolPath,
                SpoolFileHash = spoolHash,
                Color = isColor,
                Duplex = isDuplex,
                Status = "HOLD",
                Timestamp = DateTime.Now,
                Cost = CalculateCost(pages, isColor)
            };

            _db.PrintJobs.Add(printJob);
            _db.SaveChanges();

            _audit.Log("PRINT", owner, docName, $"Pagini: {pages}, Status: HOLD, Cost: {printJob.Cost:0.00}");

            // TODO: notificare client tray. PrintManagerPipeServer suportă acum operația
            // ReleaseJob, dar tot nu are canal de push service->UI - trebuie polling din UI
            // sau un canal separat inainte de a putea arata un toast "job in HOLD" in tray.
        }
        catch (Exception ex)
        {
            _audit.Log("ERROR", "SYSTEM", "PrintMonitor", ex.Message);
        }
    }

    private void TryCancel(string printerName, int jobId, string owner, string docName)
    {
        try
        {
            SpoolerControl.Cancel(printerName, jobId);
        }
        catch (Exception ex)
        {
            _audit.Log("ERROR", owner, docName, $"Nu s-a putut anula job {jobId}: {ex.Message}");
        }
    }

    private string? TryCopySpoolFile(string printerName, int jobId)
    {
        try
        {
            var spoolDir = GetSpoolDirectory(printerName);
            var srcPath = Path.Combine(spoolDir, $"{jobId:D5}.SPL");

            // Race cunoscută: dacă evenimentul WMI ajunge înainte ca spooler-ul să fi scris
            // fișierul .SPL pe disc, copia eșuează silențios - job-ul rămâne totuși HOLD/paused
            // in baza de date, doar fără spool file atașat (fără preview, dar release-ul tot
            // funcționează, pentru că se face prin SpoolerJobId, nu prin fișierul copiat).
            if (!File.Exists(srcPath))
                return null;

            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SecurePrintManager", "SpoolFiles");
            Directory.CreateDirectory(destDir);

            var destPath = Path.Combine(destDir, $"{jobId}_{Guid.NewGuid()}.spl");
            File.Copy(srcPath, destPath, overwrite: true);
            _encryption.EncryptFile(destPath);
            return destPath;
        }
        catch
        {
            return null;
        }
    }

    private static string GetSpoolDirectory(string printerName)
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "PRINTERS");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}");
            var custom = key?.GetValue("SpoolDirectory") as string;
            return string.IsNullOrWhiteSpace(custom) ? defaultPath : custom!;
        }
        catch
        {
            return defaultPath;
        }
    }

    private static string ComputeHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    private decimal CalculateCost(int pages, bool isColor)
    {
        var key = isColor ? "CostPerPageColor" : "CostPerPageBW";
        var config = _db.Configs.FirstOrDefault(c => c.Key == key);
        var costPerPage = config != null && decimal.TryParse(config.Value, out var v) ? v : 0.10m;
        return pages * costPerPage;
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EventArrived -= OnJobAdded;
            _watcher.Stop();
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
