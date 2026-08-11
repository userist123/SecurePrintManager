using System.Printing;
using System.Security.Cryptography;
using SecurePrintManager.Database;
using SecurePrintManager.Database.Models;

namespace SecurePrintManager.Core;

public class PrintMonitor : IDisposable
{
    private readonly PrintServer _printServer;
    private readonly DatabaseContext _db;
    private readonly FileEncryptionService _encryption;
    private readonly AuditLogger _audit;
    
    public PrintMonitor(DatabaseContext db, FileEncryptionService encryption, AuditLogger audit)
    {
        _db = db;
        _encryption = encryption;
        _audit = audit;
        _printServer = new LocalPrintServer();
        
        // Subscribe la evenimente
        foreach (var queue in _printServer.GetPrintQueues())
        {
            queue.JobAdded += OnJobAdded;
        }
    }
    
    private void OnJobAdded(object sender, PrintJobInfoCollection e)
    {
        try
        {
            var job = e;
            var user = job.Owner;
            var docName = job.Name;
            var pages = job.NumberOfPages;
            var printer = job.PrintingSystem.Name;
            
            // Verifică dacă user-ul există
            var dbUser = _db.Users.FirstOrDefault(u => u.Username == user);
            if (dbUser == null || !dbUser.IsActive)
            {
                _audit.Log("BLOCK", user, docName, "User not found or inactive");
                job.Cancel();
                return;
            }
            
            // Verifică quota
            if (dbUser.PagesUsed + pages > dbUser.MonthlyQuota)
            {
                _audit.Log("BLOCK", user, docName, "Quota exceeded");
                job.Cancel();
                return;
            }
            
            // Salvează spool file (encrypted)
            var spoolPath = SaveSpoolFile(job);
            
            // Creează job în DB
            var printJob = new PrintJob
            {
                UserId = dbUser.Id,
                DocumentName = docName,
                Pages = pages,
                PrinterName = printer,
                SpoolFile = spoolPath,
                SpoolFileHash = ComputeHash(spoolPath),
                Color = DetectColor(job),
                Duplex = DetectDuplex(job),
                Status = "HOLD",
                Timestamp = DateTime.Now,
                Cost = CalculateCost(pages, DetectColor(job))
            };
            
            _db.PrintJobs.Add(printJob);
            _db.SaveChanges();
            
            // Blochează job-ul
            job.Pause();
            
            // Audit
            _audit.Log("PRINT", user, docName, $"Pages: {pages}, Status: HOLD");
            
            // Notifică utilizatorul (toast notification)
            ShowNotification(user, $"Job '{docName}' trimis la secure print queue");
        }
        catch (Exception ex)
        {
            _audit.Log("ERROR", "SYSTEM", "PrintMonitor", ex.Message);
        }
    }
    
    private string SaveSpoolFile(PrintSystemJobInfo job)
    {
        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SecurePrintManager",
            "SpoolFiles"
        );
        
        Directory.CreateDirectory(tempDir);
        
        var fileName = $"{job.Id}_{Guid.NewGuid()}.spl";
        var filePath = Path.Combine(tempDir, fileName);
        
        using (var jobStream = job.GetJobStream())
        using (var fileStream = File.Create(filePath))
        {
            jobStream.CopyTo(fileStream);
        }
        
        // Encryptează fișierul
        _encryption.EncryptFile(filePath);
        
        return filePath;
    }
    
    private string ComputeHash(string filePath)
    {
        using (var sha256 = SHA256.Create())
        using (var stream = File.OpenRead(filePath))
        {
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
    
    private bool DetectColor(PrintSystemJobInfo job)
    {
        // Implementare: analizează XPS/PCL pentru color indicators
        // Sau folosește job properties
        return false; // Default B&W
    }
    
    private bool DetectDuplex(PrintSystemJobInfo job)
    {
        return job.PrintTicket?.Duplex == Duplex.TwoSidedLongEdge;
    }
    
    private decimal CalculateCost(int pages, bool isColor)
    {
        var config = _db.Config;
        var costPerPage = isColor 
            ? config.First(c => c.Key == "CostPerPageColor").Value.ToDecimal()
            : config.First(c => c.Key == "CostPerPageBW").Value.ToDecimal();
        
        return pages * costPerPage;
    }
    
    private void ShowNotification(string user, string message)
    {
        // Implementare: toast notification Windows 10/11
        // Sau tray icon notification
    }
    
    public void Dispose()
    {
        foreach (var queue in _printServer.GetPrintQueues())
        {
            queue.JobAdded -= OnJobAdded;
        }
        
        _printServer?.Dispose();
    }
}