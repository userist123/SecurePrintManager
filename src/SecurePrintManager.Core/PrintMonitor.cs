using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
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
/// Pentru detalii precise (pagini, culoare, duplex) și control (pauză/anulare) pe care WMI
/// nu le oferă fiabil, se folosește winspool.drv direct (GetJob/SetJob, nivel JOB_INFO_2,
/// cu DEVMODE pentru dmColor/dmDuplex).
/// </summary>
public class PrintMonitor : IDisposable
{
    private readonly DatabaseContext _db;
    private readonly FileEncryptionService _encryption;
    private readonly AuditLogger _audit;
    private ManagementEventWatcher? _watcher;

    private const int JOB_CONTROL_PAUSE = 1;
    private const int JOB_CONTROL_CANCEL = 3;

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
                CancelJob(printerName, jobId);
                return;
            }

            int pages;
            bool isColor, isDuplex;
            if (!TryGetJobDetails(printerName, jobId, out pages, out isColor, out isDuplex))
            {
                pages = job["TotalPages"] is uint tp && tp > 0 ? (int)tp : 1;
                isColor = false;
                isDuplex = false;
            }

            if (dbUser.PagesUsed + pages > dbUser.MonthlyQuota)
            {
                _audit.Log("BLOCK", owner, docName, $"Quota depasita ({dbUser.PagesUsed}+{pages}>{dbUser.MonthlyQuota})");
                CancelJob(printerName, jobId);
                return;
            }

            // Pauzează job-ul la nivel de spooler înainte de a ajunge la imprimantă
            PauseJob(printerName, jobId);

            var spoolPath = TryCopySpoolFile(printerName, jobId);
            var spoolHash = spoolPath != null ? ComputeHash(spoolPath) : null;

            var printJob = new PrintJob
            {
                UserId = dbUser.Id,
                DocumentName = docName,
                Pages = pages,
                PrinterName = printerName,
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

            // TODO: notificare client tray. PrintManagerPipeServer (Service.Ipc) e in prezent
            // request/response, un singur client conectat la un moment dat - nu suporta push
            // catre UI. Necesita canal separat (ex. named pipe broadcast sau polling din UI)
            // inainte de a putea arata un toast "job in HOLD" in tray.
        }
        catch (Exception ex)
        {
            _audit.Log("ERROR", "SYSTEM", "PrintMonitor", ex.Message);
        }
    }

    private bool TryGetJobDetails(string printerName, int jobId, out int pages, out bool isColor, out bool isDuplex)
    {
        pages = 0;
        isColor = false;
        isDuplex = false;

        var hPrinter = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
                return false;

            GetJob(hPrinter, jobId, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
                return false;

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetJob(hPrinter, jobId, 2, buffer, needed, out _))
                return false;

            var info = Marshal.PtrToStructure<JOB_INFO_2>(buffer);
            pages = info.TotalPages > 0 ? info.TotalPages : 1;

            if (info.pDevMode != IntPtr.Zero)
            {
                var dm = Marshal.PtrToStructure<DEVMODE>(info.pDevMode);
                isColor = dm.dmColor == 2; // DMCOLOR_COLOR
                isDuplex = dm.dmDuplex is 2 or 3; // DMDUP_VERTICAL / DMDUP_HORIZONTAL
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }
    }

    private static void PauseJob(string printerName, int jobId) => SetJobControl(printerName, jobId, JOB_CONTROL_PAUSE);

    private static void CancelJob(string printerName, int jobId) => SetJobControl(printerName, jobId, JOB_CONTROL_CANCEL);

    private static void SetJobControl(string printerName, int jobId, int command)
    {
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            return;
        try
        {
            SetJob(hPrinter, jobId, 0, IntPtr.Zero, command);
        }
        finally
        {
            ClosePrinter(hPrinter);
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
            // in baza de date, doar fără spool file atașat (nu poate fi redirecționat la release).
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

    #region winspool.drv interop

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetJob(IntPtr hPrinter, int jobId, int level, IntPtr pJob, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetJob(IntPtr hPrinter, int jobId, int level, IntPtr pJob, int command);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOB_INFO_2
    {
        public int JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pNotifyName;
        public IntPtr pDatatype;
        public IntPtr pPrintProcessor;
        public IntPtr pParameters;
        public IntPtr pDriverName;
        public IntPtr pDevMode;
        public IntPtr pStatus;
        public IntPtr pSecurityDescriptor;
        public int Status;
        public int Priority;
        public int Position;
        public int StartTime;
        public int UntilTime;
        public int TotalPages;
        public int Size;
        public System.Runtime.InteropServices.ComTypes.FILETIME Submitted;
        public int Time;
        public int PagesPrinted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    #endregion
}
