using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SecurePrintManager.Core;
using SecurePrintManager.Database;

namespace SecurePrintManager.UI;

public partial class HistoryWindow : Window
{
    private readonly DatabaseContext _db;
    private readonly User _currentUser;
    private readonly bool _isAdmin;

    public HistoryWindow(User currentUser, bool isAdmin = false)
    {
        InitializeComponent();

        _db = new DatabaseContext();
        _currentUser = currentUser;
        _isAdmin = isAdmin;

        // Set default dates (last 30 days)
        FromDatePicker.SelectedDate = DateTime.Now.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Now;

        LoadHistory();
    }

    private void LoadHistory()
    {
        var fromDate = FromDatePicker.SelectedDate ?? DateTime.Now.AddDays(-30);
        var toDate = ToDatePicker.SelectedDate ?? DateTime.Now;
        var selectedType = (TypeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

        var printJobs = _db.PrintJobs
            .Where(j => j.Timestamp >= fromDate && j.Timestamp <= toDate)
            .Where(j => !_isAdmin || j.UserId == _currentUser.Id)
            .Select(j => new HistoryItem
            {
                Timestamp = j.Timestamp,
                Type = "Print",
                DocumentName = j.DocumentName,
                Pages = j.Pages,
                Destination = j.PrinterName,
                Status = j.Status,
                Cost = j.Cost,
                UserId = j.UserId
            })
            .ToList();

        var scanJobs = _db.ScanJobs
            .Where(s => s.Timestamp >= fromDate && s.Timestamp <= toDate)
            .Where(s => !_isAdmin || s.UserId == _currentUser.Id)
            .Select(s => new HistoryItem
            {
                Timestamp = s.Timestamp,
                Type = "Scan",
                DocumentName = $"Scan-{s.Id}",
                Pages = s.Pages,
                Destination = s.Destination ?? "N/A",
                Status = "Completed",
                Cost = s.Cost,
                UserId = s.UserId
            })
            .ToList();

        var allJobs = printJobs.Concat(scanJobs)
            .OrderByDescending(j => j.Timestamp)
            .ToList();

        // Filter by type if selected
        if (selectedType == "Print")
            allJobs = allJobs.Where(j => j.Type == "Print").ToList();
        else if (selectedType == "Scan")
            allJobs = allJobs.Where(j => j.Type == "Scan").ToList();

        HistoryDataGrid.ItemsSource = allJobs;
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        LoadHistory();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        FromDatePicker.SelectedDate = DateTime.Now.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Now;
        TypeComboBox.SelectedIndex = 0;
        LoadHistory();
    }

    private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Optional: auto-filter on selection change
    }

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"history_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dlg.ShowDialog() != true) return;

        var items = HistoryDataGrid.ItemsSource.Cast<HistoryItem>().ToList();

        var csv = new StringBuilder();
        csv.AppendLine("Timestamp,Type,Document,Pages,Destination,Status,Cost");

        foreach (var item in items)
        {
            csv.AppendLine($"{item.Timestamp:O},{item.Type},{item.DocumentName},{item.Pages},{item.Destination},{item.Status},{item.Cost}");
        }

        File.WriteAllText(dlg.FileName, csv.ToString());
        MessageBox.Show($"Istoric exportat în {dlg.FileName}", "Export complet",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class HistoryItem
{
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    public int Pages { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    public int UserId { get; set; }
}
