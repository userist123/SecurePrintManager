using System.Linq;
using System.Windows;
using Microsoft.Win32;
using SecurePrintManager.Core;
using SecurePrintManager.Database;

namespace SecurePrintManager.UI;

public partial class AdminWindow : Window
{
    private readonly DatabaseContext _db;
    private readonly User _currentUser;
    private readonly AuditLogger _auditLogger;

    public AdminWindow(User currentUser)
    {
        InitializeComponent();

        _db = new DatabaseContext();
        _currentUser = currentUser;
        _auditLogger = new AuditLogger(_db);

        LoadUsers();
        LoadSettings();
    }

    private void LoadUsers()
    {
        UsersDataGrid.ItemsSource = _db.Users.OrderBy(u => u.Username).ToList();
    }

    private User? GetSelectedUser() => UsersDataGrid.SelectedItem as User;

    private void AddUser_Click(object sender, RoutedEventArgs e)
    {
        // Poți face un dialog simplu cu TextBox-uri sau un UserEditWindow
        // Ca exemplu minimal:
        var username = Microsoft.VisualBasic.Interaction.InputBox("Username:", "Add user");
        if (string.IsNullOrWhiteSpace(username)) return;

        var user = new User
        {
            Username = username,
            FullName = username,
            MonthlyQuota = 100,
            ScanQuota = 50,
            IsActive = true
        };
        _db.Users.Add(user);
        _db.SaveChanges();

        _auditLogger.Log("USER_ADD", _currentUser.Username, null, $"User {username} added.");
        LoadUsers();
    }

    private void EditUser_Click(object sender, RoutedEventArgs e)
    {
        var user = GetSelectedUser();
        if (user == null) return;

        var quotaStr = Microsoft.VisualBasic.Interaction.InputBox(
            $"Monthly quota for {user.Username}:", "Edit user", user.MonthlyQuota.ToString());
        if (int.TryParse(quotaStr, out var quota))
        {
            user.MonthlyQuota = quota;
            _db.SaveChanges();
            _auditLogger.Log("USER_EDIT", _currentUser.Username, null,
                $"User {user.Username} quota set to {quota}.");
            LoadUsers();
        }
    }

    private void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        var user = GetSelectedUser();
        if (user == null) return;

        if (MessageBox.Show($"Delete user {user.Username}?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _db.Users.Remove(user);
        _db.SaveChanges();
        _auditLogger.Log("USER_DELETE", _currentUser.Username, null,
            $"User {user.Username} deleted.");
        LoadUsers();
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var lines = System.IO.File.ReadAllLines(dlg.FileName);
        foreach (var line in lines.Skip(1)) // skip header
        {
            var parts = line.Split(',');
            if (parts.Length < 5) continue;

            var username = parts[0].Trim();
            var fullName = parts[1].Trim();
            var pin = parts[2].Trim();
            var card = parts[3].Trim();
            var quota = int.Parse(parts[4].Trim());

            if (_db.Users.Any(u => u.Username == username))
                continue;

            var user = new User
            {
                Username = username,
                FullName = fullName,
                PinCode = string.IsNullOrEmpty(pin) ? null : pin,
                CardCode = string.IsNullOrEmpty(card) ? null : card,
                MonthlyQuota = quota,
                ScanQuota = 50,
                IsActive = true
            };
            _db.Users.Add(user);
        }

        _db.SaveChanges();
        _auditLogger.Log("IMPORT_CSV", _currentUser.Username, null,
            $"Users imported from {dlg.FileName}.");
        LoadUsers();
    }

    private void LoadSettings()
    {
        string GetConfig(string key) =>
            _db.Configs.FirstOrDefault(c => c.Key == key)?.Value ?? "";

        JobTimeoutTextBox.Text = GetConfig("JobTimeoutHours");
        SessionTimeoutTextBox.Text = GetConfig("SessionTimeoutMinutes");
        CostBwTextBox.Text = GetConfig("CostPerPageBW");
        CostColorTextBox.Text = GetConfig("CostPerPageColor");
        CostScanTextBox.Text = GetConfig("CostPerScan");

        EnableEncryptionCheckBox.IsChecked = GetConfig("EnableEncryption") == "true";
        EnableAuditLogCheckBox.IsChecked = GetConfig("EnableAuditLog") == "true";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        void SetConfig(string key, string value)
        {
            var cfg = _db.Configs.FirstOrDefault(c => c.Key == key);
            if (cfg == null)
            {
                cfg = new Config { Key = key, Value = value, UpdatedAt = DateTime.Now };
                _db.Configs.Add(cfg);
            }
            else
            {
                cfg.Value = value;
                cfg.UpdatedAt = DateTime.Now;
            }
        }

        SetConfig("JobTimeoutHours", JobTimeoutTextBox.Text);
        SetConfig("SessionTimeoutMinutes", SessionTimeoutTextBox.Text);
        SetConfig("CostPerPageBW", CostBwTextBox.Text);
        SetConfig("CostPerPageColor", CostColorTextBox.Text);
        SetConfig("CostPerScan", CostScanTextBox.Text);
        SetConfig("EnableEncryption", EnableEncryptionCheckBox.IsChecked == true ? "true" : "false");
        SetConfig("EnableAuditLog", EnableAuditLogCheckBox.IsChecked == true ? "true" : "false");

        _db.SaveChanges();
        _auditLogger.Log("CONFIG_CHANGE", _currentUser.Username, null,
            "Settings updated from AdminPanel.");
        MessageBox.Show("Settings saved.", "Info",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}