using System;

namespace SecurePrintManager.Core;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string? PinCode { get; set; }
    public string? CardCode { get; set; }
    public string? Department { get; set; }
    public int MonthlyQuota { get; set; } = 100;
    public int PagesUsed { get; set; }
    public int ScanQuota { get; set; } = 50;
    public int ScansUsed { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastLogin { get; set; }
    public DateTime? LastQuotaReset { get; set; }
}

public class PrintJob
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public int Pages { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public string? SpoolFile { get; set; }
    public string? SpoolFileHash { get; set; }
    public bool Color { get; set; }
    public bool Duplex { get; set; }
    public string Status { get; set; } = "HOLD";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public DateTime? PrintedAt { get; set; }
    public string? ReleasedBy { get; set; }
    public decimal Cost { get; set; }
}

public class ScanJob
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int Pages { get; set; }
    public string? Destination { get; set; }
    public string? FilePath { get; set; }
    public decimal Cost { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class AuditLog
{
    public int Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? DocumentName { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? PreviousHash { get; set; }
    public string CurrentHash { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class Config
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class Quota
{
    public int UserId { get; set; }
    public int MonthlyQuota { get; set; }
    public int PagesUsed { get; set; }
    public int ScanQuota { get; set; }
    public int ScansUsed { get; set; }
    public DateTime LastReset { get; set; }
}
