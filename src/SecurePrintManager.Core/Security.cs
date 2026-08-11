using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BCrypt.Net;
using SecurePrintManager.Database;

namespace SecurePrintManager.Core;

public class AuthenticationService
{
    private readonly DatabaseContext _db;

    public AuthenticationService(DatabaseContext db)
    {
        _db = db;
    }

    public AuthResult AuthenticateByPin(string pin)
    {
        var user = _db.Users.FirstOrDefault(u => u.PinCode == pin && u.IsActive);
        if (user == null)
            return new AuthResult(false, "PIN invalid sau utilizator inactiv");

        user.LastLogin = DateTime.Now;
        _db.SaveChanges();

        return new AuthResult(true, "Autentificare reușită", user);
    }

    public AuthResult AuthenticateByCard(string cardCode)
    {
        var user = _db.Users.FirstOrDefault(u => u.CardCode == cardCode && u.IsActive);
        if (user == null)
            return new AuthResult(false, "Card nevalid sau utilizator inactiv");

        user.LastLogin = DateTime.Now;
        _db.SaveChanges();

        return new AuthResult(true, "Autentificare reușită", user);
    }

    public AuthResult AuthenticateByUsernamePassword(string username, string password)
    {
        var user = _db.Users.FirstOrDefault(u => u.Username == username && u.IsActive);
        if (user == null || string.IsNullOrEmpty(user.PasswordHash))
            return new AuthResult(false, "Utilizator invalid");

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return new AuthResult(false, "Parolă incorectă");

        user.LastLogin = DateTime.Now;
        _db.SaveChanges();

        return new AuthResult(true, "Autentificare reușită", user);
    }
}

public class AuthResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public User? User { get; set; }

    public AuthResult(bool success, string message, User? user = null)
    {
        Success = success;
        Message = message;
        User = user;
    }
}

public class CardReader
{
    private System.IO.Ports.SerialPort? _serialPort;
    public event Action<string>? CardRead;

    public void Initialize(string portName, int baudRate)
    {
        try
        {
            _serialPort = new System.IO.Ports.SerialPort(portName, baudRate);
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Eroare la inițializarea cititorului de carduri: {ex.Message}");
        }
    }

    private void SerialPort_DataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null) return;

        try
        {
            var cardCode = _serialPort.ReadLine().Trim();
            CardRead?.Invoke(cardCode);
        }
        catch { }
    }

    public void Close()
    {
        if (_serialPort != null && _serialPort.IsOpen)
        {
            _serialPort.DataReceived -= SerialPort_DataReceived;
            _serialPort.Close();
            _serialPort.Dispose();
        }
    }
}

public class FileEncryptionService
{
    private readonly byte[] _key;
    private readonly byte[] _iv;

    public FileEncryptionService()
    {
        // În producție, salvează cheia într-un loc sigur (Windows DPAPI, Azure Key Vault, etc.)
        var keyBytes = Encoding.UTF8.GetBytes("SecurePrintManager2026Key32Bytes!");
        _key = keyBytes;
        _iv = Encoding.UTF8.GetBytes("SecurePrintIV16B");
    }

    public void EncryptFile(string filePath)
    {
        var plainBytes = File.ReadAllBytes(filePath);
        var encryptedBytes = Encrypt(plainBytes);
        File.WriteAllBytes(filePath, encryptedBytes);
    }

    public void DecryptFile(string filePath)
    {
        var encryptedBytes = File.ReadAllBytes(filePath);
        var decryptedBytes = Decrypt(encryptedBytes);
        File.WriteAllBytes(filePath, decryptedBytes);
    }

    private byte[] Encrypt(byte[] plainBytes)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;

        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
        {
            cs.Write(plainBytes, 0, plainBytes.Length);
        }
        return ms.ToArray();
    }

    private byte[] Decrypt(byte[] encryptedBytes)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;

        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
        {
            cs.Write(encryptedBytes, 0, encryptedBytes.Length);
        }
        return ms.ToArray();
    }
}

public class AuditLogger
{
    private readonly DatabaseContext _db;
    private string? _lastHash;

    public AuditLogger(DatabaseContext db)
    {
        _db = db;
        _lastHash = GetLastHash();
    }

    private string? GetLastHash()
    {
        var lastLog = _db.AuditLogs.OrderByDescending(a => a.Timestamp).FirstOrDefault();
        return lastLog?.CurrentHash;
    }

    public void Log(string action, string? username, string? documentName, string details)
    {
        var entry = new AuditLog
        {
            Action = action,
            Username = username,
            DocumentName = documentName,
            Details = details,
            IpAddress = Environment.MachineName,
            PreviousHash = _lastHash,
            CurrentHash = ComputeHash(action, username, documentName, details, _lastHash, DateTime.Now),
            Timestamp = DateTime.Now
        };

        _db.AuditLogs.Add(entry);
        _db.SaveChanges();

        _lastHash = entry.CurrentHash;
    }

    private string ComputeHash(string action, string? username, string? documentName, string details, string? previousHash, DateTime timestamp)
    {
        var data = $"{action}|{username}|{documentName}|{details}|{previousHash}|{timestamp:O}";
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
    }
}

public class QuotaManager
{
    private readonly DatabaseContext _db;

    public QuotaManager(DatabaseContext db)
    {
        _db = db;
    }

    public bool CheckQuota(int userId, int pagesNeeded)
    {
        var user = _db.Users.Find(userId);
        if (user == null) return false;

        // Reset lunar automat
        if (user.LastQuotaReset == null || user.LastQuotaReset.Value.Month != DateTime.Now.Month)
        {
            ResetMonthlyQuota(user);
        }

        return user.PagesUsed + pagesNeeded <= user.MonthlyQuota;
    }

    public void UseQuota(int userId, int pagesUsed)
    {
        var user = _db.Users.Find(userId);
        if (user == null) return;

        user.PagesUsed += pagesUsed;
        _db.SaveChanges();
    }

    public void ResetMonthlyQuota(User user)
    {
        user.PagesUsed = 0;
        user.ScansUsed = 0;
        user.LastQuotaReset = DateTime.Now;
        _db.SaveChanges();
    }

    public decimal GetQuotaUsagePercent(int userId)
    {
        var user = _db.Users.Find(userId);
        if (user == null) return 0;

        return (decimal)user.PagesUsed / user.MonthlyQuota * 100;
    }
}
