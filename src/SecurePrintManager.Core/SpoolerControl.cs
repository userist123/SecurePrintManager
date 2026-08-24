using System;
using System.Runtime.InteropServices;

namespace SecurePrintManager.Core;

/// <summary>
/// Interop partajat peste winspool.drv pentru control job (pauză/reluare/anulare) și
/// citire metadate (pagini/culoare/duplex din DEVMODE). Folosit atât de PrintMonitor
/// (la intercepare) cât și de handler-ul IPC de release (la eliberarea job-ului).
/// </summary>
public static class SpoolerControl
{
    public const int JOB_CONTROL_PAUSE = 1;
    public const int JOB_CONTROL_RESUME = 2;
    public const int JOB_CONTROL_CANCEL = 3;

    public static void Pause(string printerName, int jobId) => SetControl(printerName, jobId, JOB_CONTROL_PAUSE);

    public static void Resume(string printerName, int jobId) => SetControl(printerName, jobId, JOB_CONTROL_RESUME);

    public static void Cancel(string printerName, int jobId) => SetControl(printerName, jobId, JOB_CONTROL_CANCEL);

    private static void SetControl(string printerName, int jobId, int command)
    {
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            throw new InvalidOperationException($"OpenPrinter a eșuat pentru '{printerName}' (Win32 error {Marshal.GetLastWin32Error()})");
        try
        {
            if (!SetJob(hPrinter, jobId, 0, IntPtr.Zero, command))
                throw new InvalidOperationException($"SetJob({command}) a eșuat pentru job {jobId} pe '{printerName}' (Win32 error {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    public static bool TryGetJobDetails(string printerName, int jobId, out int pages, out bool isColor, out bool isDuplex)
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
}
