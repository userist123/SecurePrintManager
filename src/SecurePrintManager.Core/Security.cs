using System.Security.Cryptography;
using System.Text;
using BCrypt.Net;
using SecurePrintManager.Database;

namespace SecurePrintManager.Core;

public sealed class AuthenticationService
{
    private readonly DatabaseContext _db;

    public AuthenticationService(DatabaseContext db) => _db = db;

    public AuthResult AuthenticateByPin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return AuthResult.Failure("Credențială invalidă");
        foreach (var user in _db.Users.Where(u => u.IsActive && !string.IsNullOrEmpty(u.PinCode)).ToList())
        {
            var stored = user.PinCode;
            if (!VerifyAndUpgrade(pin.Trim(), stored, out var upgraded)) continue;
            if (!string.Equals(stored, upgraded, StringComparison.Ordinal))
                user.PinCode = upgraded;
            user.LastLogin = DateTime.UtcNow;
            _db.SaveChanges();
            return AuthResult.Successful(user);
        }
        return AuthResult.Failure("PIN invalid sau utilizator inactiv");
    }

    public AuthResult AuthenticateByCard(string cardCode)
    {
        if (string.IsNullOrWhiteSpace(cardCode)) return AuthResult.Failure("Card invalid");
        foreach (var user in _db.Users.Where(u => u.IsActive && !string.IsNullOrEmpty(u.CardCode)).ToList())
        {
            var stored = user.CardCode;
            if (!VerifyAndUpgrade(cardCode.Trim(), stored, out var upgraded)) continue;
            if (!string.Equals(stored, upgraded, StringComparison.Ordinal))
                user.CardCode = upgraded;
            user.LastLogin = DateTime.UtcNow;
            _db.SaveChanges();
            return AuthResult.Successful(user);
        }
        return AuthResult.Failure("Card nevalid sau utilizator inactiv");
    }

    public AuthResult AuthenticateByUsernamePassword(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return AuthResult.Failure("Utilizator sau parolă invalidă");
        var user = _db.Users.FirstOrDefault(u => u.IsActive && u.Username == username);
        if (user == null || string.IsNullOrEmpty(user.PasswordHash) || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return AuthResult.Failure("Utilizator sau parolă invalidă");
        user.LastLogin = DateTime.UtcNow;
        _db.SaveChanges();
        return AuthResult.Successful(user);
    }

    private static bool VerifyAndUpgrade(string candidate, string? stored, out string? upgraded)
    {
        upgraded = stored;
        if (string.IsNullOrWhiteSpace(stored)) return false;

        // New format: BCrypt hash.
        if (stored.StartsWith("$2", StringComparison.Ordinal))
            return BCrypt.Net.BCrypt.Verify(candidate, stored);

        // Legacy plaintext is accepted only for migration. Successful authentication
        // immediately replaces it with a BCrypt hash before the next use.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(stored)))
            return false;

        upgraded = BCrypt.Net.BCrypt.HashPassword(candidate);
        return true;
    }
}

public sealed class AuthResult
{
    public bool Success { get; }
    public string Message { get; }
    public User? User { get; }

    private AuthResult(bool success, string message, User? user = null)
    {
        Success = success;
        Message = message;
        User = user;
    }

    public static AuthResult Successful(User user) => new(true, "Autentificare reușită", user);
    public static AuthResult Failure(string message) => new(false, message);
}

public sealed class CardReader : IDisposable
{
    private System.IO.Ports.SerialPort? _serialPort;
    public event Action<string>? CardRead;

    public void Initialize(string portName, int baudRate)
    {
        Close();
        _serialPort = new System.IO.Ports.SerialPort(portName, baudRate);
        _serialPort.DataReceived += SerialPort_DataReceived;
        _serialPort.Open();
    }

    private void SerialPort_DataReceived(object? sender, System.IO.Ports.SerialDataReceivedEventArgs e)
    {
        try
        {
            var code = _serialPort?.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(code)) CardRead?.Invoke(code);
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    public void Close()
    {
        if (_serialPort == null) return;
        _serialPort.DataReceived -= SerialPort_DataReceived;
        if (_serialPort.IsOpen) _serialPort.Close();
        _serialPort.Dispose();
        _serialPort = null;
    }

    public void Dispose() => Close();
}

public sealed class FileEncryptionService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SPM1");
    private readonly byte[] _key;

    public FileEncryptionService()
    {
        var keyMaterial = Environment.GetEnvironmentVariable("SECUREPRINT_FILE_KEY");
        if (string.IsNullOrWhiteSpace(keyMaterial))
            throw new InvalidOperationException("SECUREPRINT_FILE_KEY is not configured. Provision the key outside source control with Windows-protected service configuration or an enterprise secret store.");
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
    }

    public void EncryptFile(string filePath)
    {
        var plainBytes = File.ReadAllBytes(filePath);
        File.WriteAllBytes(filePath, Encrypt(plainBytes));
    }

    public void DecryptFile(string filePath)
    {
        var encryptedBytes = File.ReadAllBytes(filePath);
        File.WriteAllBytes(filePath, Decrypt(encryptedBytes));
    }

    private byte[] Encrypt(byte[] plainBytes)
    {
        using var aes = new AesGcm(_key, 16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plainBytes.Length];
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.WriteByte((byte)nonce.Length);
        ms.WriteByte((byte)tag.Length);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(cipher);
        return ms.ToArray();
    }

    private byte[] Decrypt(byte[] encryptedBytes)
    {
        if (encryptedBytes.Length < Magic.Length + 2 + 12 + 16 || !encryptedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Fișierul nu are un format SecurePrintManager valid.");
        var nonceLength = encryptedBytes[Magic.Length];
        var tagLength = encryptedBytes[Magic.Length + 1];
        var offset = Magic.Length + 2;
        if (nonceLength <= 0 || tagLength <= 0 || encryptedBytes.Length < offset + nonceLength + tagLength)
            throw new CryptographicException("Metadate de criptare invalide.");
        var nonce = encryptedBytes.AsSpan(offset, nonceLength).ToArray();
        offset += nonceLength;
        var tag = encryptedBytes.AsSpan(offset, tagLength).ToArray();
        offset += tagLength;
        var cipher = encryptedBytes.AsSpan(offset).ToArray();
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tagLength);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

public sealed class AuditLogger
{
    private readonly DatabaseContext _db;
    private readonly object _sync = new();
    private string? _lastHash;

    public AuditLogger(DatabaseContext db)
    {
        _db = db;
        _lastHash = GetLastHash();
    }

    private string? GetLastHash() => _db.AuditLogs.OrderByDescending(a => a.Id).Select(a => a.CurrentHash).FirstOrDefault();

    public void Log(string action, string? username, string? documentName, string details)
    {
        lock (_sync)
        {
            var timestamp = DateTime.UtcNow;
            var currentHash = ComputeHash(action, username, documentName, details, _lastHash, timestamp);
            var entry = new AuditLog
            {
                Action = action,
                Username = username,
                DocumentName = documentName,
                Details = details,
                IpAddress = Environment.MachineName,
                PreviousHash = _lastHash,
                CurrentHash = currentHash,
                Timestamp = timestamp
            };
            _db.AuditLogs.Add(entry);
            _db.SaveChanges();
            _lastHash = currentHash;
        }
    }

    private static string ComputeHash(string action, string? username, string? documentName, string details, string? previousHash, DateTime timestamp)
    {
        var data = $"{action}|{username}|{documentName}|{details}|{previousHash}|{timestamp:O}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }
}

public sealed class QuotaManager
{
    private readonly DatabaseContext _db;
    public QuotaManager(DatabaseContext db) => _db = db;

    public bool CheckQuota(int userId, int pagesNeeded)
    {
        var user = _db.Users.Find(userId);
        if (user == null || pagesNeeded < 0) return false;
        EnsureMonthlyReset(user);
        return user.PagesUsed + pagesNeeded <= user.MonthlyQuota;
    }

    public void UseQuota(int userId, int pagesUsed)
    {
        if (pagesUsed < 0) throw new ArgumentOutOfRangeException(nameof(pagesUsed));
        var user = _db.Users.Find(userId) ?? throw new InvalidOperationException("Utilizator inexistent.");
        EnsureMonthlyReset(user);
        user.PagesUsed += pagesUsed;
        _db.SaveChanges();
    }

    public void ResetMonthlyQuota(User user)
    {
        user.PagesUsed = 0;
        user.ScansUsed = 0;
        user.LastQuotaReset = DateTime.UtcNow;
        _db.SaveChanges();
    }

    public decimal GetQuotaUsagePercent(int userId)
    {
        var user = _db.Users.Find(userId);
        if (user == null || user.MonthlyQuota <= 0) return 0;
        EnsureMonthlyReset(user);
        return Math.Min(100m, user.PagesUsed * 100m / user.MonthlyQuota);
    }

    private void EnsureMonthlyReset(User user)
    {
        var now = DateTime.UtcNow;
        if (user.LastQuotaReset == null || user.LastQuotaReset.Value.Year != now.Year || user.LastQuotaReset.Value.Month != now.Month)
            ResetMonthlyQuota(user);
    }
}
