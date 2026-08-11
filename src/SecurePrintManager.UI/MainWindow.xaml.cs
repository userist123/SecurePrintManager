using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SecurePrintManager.Core;
using SecurePrintManager.Database;

namespace SecurePrintManager.UI;

public partial class MainWindow : Window
{
    private readonly DatabaseContext _db;
    private readonly User _currentUser;
    private readonly QuotaManager _quotaManager;
    private readonly AuditLogger _auditLogger;
    private readonly FileEncryptionService _encryption;
    private string? _currentPreviewPdfPath;

    public MainWindow(User currentUser)
    {
        InitializeComponent();

        _db = new DatabaseContext();
        _currentUser = currentUser;
        _quotaManager = new QuotaManager(_db);
        _auditLogger = new AuditLogger(_db);
        _encryption = new FileEncryptionService();

        LoadUserInfo();
        LoadPrintQueue();
    }

    private void LoadUserInfo()
    {
        UserNameText.Text = $"{_currentUser.FullName} ({_currentUser.Username})";
        DepartmentText.Text = string.IsNullOrEmpty(_currentUser.Department)
            ? "Department: N/A"
            : $"Department: {_currentUser.Department}";

        QuotaText.Text = $"Quota: {_currentUser.PagesUsed}/{_currentUser.MonthlyQuota} pages";
        var percent = _quotaManager.GetQuotaUsagePercent(_currentUser.Id);
        QuotaProgressBar.Value = (double)percent;

        LastLoginText.Text = $"Last login: {_currentUser.LastLogin}";
        PagesUsedText.Text = $"Pages used: {_currentUser.PagesUsed}";
        ScansUsedText.Text = $"Scans used: {_currentUser.ScansUsed}";
    }

    private void LoadPrintQueue()
    {
        var jobs = _db.PrintJobs
            .Where(j => j.UserId == _currentUser.Id && j.Status == "HOLD")
            .OrderBy(j => j.Timestamp)
            .ToList();

        PrintQueueDataGrid.ItemsSource = jobs;
        StatusText.Text = $"Jobs in queue: {jobs.Count}";
    }

    private PrintJob? GetSelectedJob()
    {
        return PrintQueueDataGrid.SelectedItem as PrintJob;
    }

    private void PrintQueueDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var job = GetSelectedJob();
        if (job == null)
        {
            PreviewInfoText.Visibility = Visibility.Visible;
            PreviewWebBrowser.Visibility = Visibility.Collapsed;
            PreviewMetaText.Text = string.Empty;
            ClearPreviewPdf();
            return;
        }

        PreviewMetaText.Text = $"Pagini: {job.Pages}, Printer: {job.PrinterName}, Cost: {job.Cost:0.00} RON";

        // Încearcă să afișeze preview PDF
        ShowPreviewForJob(job);
    }

    private void ShowPreviewForJob(PrintJob job)
    {
        if (string.IsNullOrEmpty(job.SpoolFile) || !File.Exists(job.SpoolFile))
        {
            PreviewInfoText.Text = "Fișierul spool nu există. Preview indisponibil.";
            PreviewInfoText.Visibility = Visibility.Visible;
            PreviewWebBrowser.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            // Decriptează fișierul spool într-un temp file
            var tempSpoolPath = Path.Combine(Path.GetTempPath(), $"spool_{job.Id}.spl");
            File.Copy(job.SpoolFile, tempSpoolPath, true);
            _encryption.DecryptFile(tempSpoolPath);

            // Convertește spool → PDF (skeleton - în producție vei folosi un converter real)
            var pdfPath = ConvertSpoolToPdf(tempSpoolPath, job.Id);

            if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
            {
                _currentPreviewPdfPath = pdfPath;
                PreviewWebBrowser.Navigate(new Uri(pdfPath));
                PreviewWebBrowser.Visibility = Visibility.Visible;
                PreviewInfoText.Visibility = Visibility.Collapsed;
                PreviewInfoText.Text = job.DocumentName;
            }
            else
            {
                PreviewInfoText.Text = "Conversia în PDF nu este disponibilă în acest moment.";
                PreviewInfoText.Visibility = Visibility.Visible;
                PreviewWebBrowser.Visibility = Visibility.Collapsed;
            }

            // Curăță temp spool
            if (File.Exists(tempSpoolPath))
                File.Delete(tempSpoolPath);
        }
        catch (Exception ex)
        {
            PreviewInfoText.Text = $"Eroare la încărcarea preview: {ex.Message}";
            PreviewInfoText.Visibility = Visibility.Visible;
            PreviewWebBrowser.Visibility = Visibility.Collapsed;
        }
    }

    private string? ConvertSpoolToPdf(string spoolPath, int jobId)
    {
        // În producție, aici vei folosi:
        // - Un motor de randare XPS/PCL (ex: GhostPCL, XpsConverter, sau un service extern)
        // - Sau salvezi direct PDF la print time (dacă driver-ul suportă "Print to PDF")

        // Pentru moment, returnăm un PDF dummy (sau poți genera un PDF minimal)
        var pdfPath = Path.Combine(Path.GetTempPath(), $"preview_{jobId}.pdf");

        // Exemplu: creează un PDF minimal cu numele documentului
        CreateMinimalPdf(pdfPath, "Preview indisponibil - fișier spool");

        return pdfPath;
    }

    private void CreateMinimalPdf(string pdfPath, string text)
    {
        // PDF minimal (1 obiect, 1 pagină, text centrat)
        // Acesta este un PDF extrem de simplu, doar pentru demo
        var content = $@"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>
endobj
4 0 obj
<< /Length 44 >>
stream
BT
/F1 24 Tf
100 700 Td
({text}) Tj
ET
endstream
endobj
5 0 obj
<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
endobj
xref
0 6
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000115 00000 n 
0000000234 00000 n 
0000000326 00000 n 
trailer
<< /Size 6 /Root 1 0 R >>
startxref
399
%%EOF
";
        File.WriteAllText(pdfPath, content);
    }

    private void ClearPreviewPdf()
    {
        if (!string.IsNullOrEmpty(_currentPreviewPdfPath) && File.Exists(_currentPreviewPdfPath))
        {
            try
            {
                File.Delete(_currentPreviewPdfPath);
            }
            catch { }
            _currentPreviewPdfPath = null;
        }
    }

    private void PrintSelectedJob_Click(object sender, RoutedEventArgs e)
    {
        var job = GetSelectedJob();
        if (job == null)
        {
            MessageBox.Show("Selectează un job din listă.", "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ClearPreviewPdf();

        job.Status = "PRINTED";
        job.PrintedAt = DateTime.Now;
        job.ReleasedBy = _currentUser.Username;

        _quotaManager.UseQuota(_currentUser.Id, job.Pages);
        _db.SaveChanges();

        _auditLogger.Log("RELEASE", _currentUser.Username, job.DocumentName,
            $"Job {job.Id} printed. Pages: {job.Pages}");

        LoadUserInfo();
        LoadPrintQueue();
    }

    private void DeleteSelectedJob_Click(object sender, RoutedEventArgs e)
    {
        var job = GetSelectedJob();
        if (job == null)
        {
            MessageBox.Show("Selectează un job din listă.", "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Ștergi job-ul '{job.DocumentName}'?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        ClearPreviewPdf();

        job.Status = "DELETED";
        _db.SaveChanges();

        _auditLogger.Log("DELETE", _currentUser.Username, job.DocumentName,
            $"Job {job.Id} deleted from queue.");

        LoadPrintQueue();
    }

    private void PrintQueueButton_Click(object sender, RoutedEventArgs e) => LoadPrintQueue();

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var historyWindow = new HistoryWindow(_currentUser, _currentUser.IsAdmin);
        historyWindow.ShowDialog();
    }

    private void ReportsButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Reports window to be implemented.", "Info",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_currentUser.IsAdmin)
        {
            MessageBox.Show(
                "Nu ai permisiuni de administrator pentru a accesa Admin Panel.",
                "Access denied",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var adminWindow = new AdminWindow(_currentUser);
        adminWindow.ShowDialog();
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearPreviewPdf();
        base.OnClosed(e);
    }
}
