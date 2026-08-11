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

    public MainWindow(User currentUser)
    {
        InitializeComponent();

        _db = new DatabaseContext();
        _currentUser = currentUser;
        _quotaManager = new QuotaManager(_db);
        _auditLogger = new AuditLogger(_db);

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
            PreviewInfoText.Text = "Selectează un job pentru a vedea detalii (PDF/imagine).";
            PreviewMetaText.Text = string.Empty;
            return;
        }

        // Pentru moment afișăm doar meta; ulterior poți deschide PDF/imaginea din SpoolFile
        PreviewInfoText.Text = job.DocumentName;
        PreviewMetaText.Text = $"Pagini: {job.Pages}, Printer: {job.PrinterName}, Cost: {job.Cost:0.00} RON";
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
        // Placeholder: aici poți deschide o fereastră de rapoarte cu grafice
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
}
