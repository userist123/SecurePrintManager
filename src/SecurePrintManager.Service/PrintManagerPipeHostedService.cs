using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecurePrintManager.Core;
using SecurePrintManager.Core.Ipc;
using SecurePrintManager.Database;

namespace SecurePrintManager.Service;

public sealed class PrintManagerPipeHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PrintManagerPipeHostedService> _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public PrintManagerPipeHostedService(IServiceScopeFactory scopeFactory, ILogger<PrintManagerPipeHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanupExpiredSessions();
                using var pipe = new NamedPipeServerStream(
                    PrintManagerProtocol.PipeName,
                    PipeDirection.InOut,
                    8,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(stoppingToken);
                var request = PrintManagerProtocol.DeserializeRequest(await NamedPipeFrame.ReadAsync(pipe, stoppingToken));
                var response = await HandleAsync(request, stoppingToken);
                await NamedPipeFrame.WriteAsync(pipe, PrintManagerProtocol.Serialize(response), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SecurePrintManager IPC request failed.");
            }
        }
    }

    private async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        if (request.Version != PrintManagerProtocol.CurrentVersion)
            return Error(request, "UNSUPPORTED_VERSION", "Unsupported IPC protocol version.");

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 64)
            return Error(request, "INVALID_REQUEST", "Invalid request id.");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            if (request.Operation == "health")
                return Ok(request, new { status = "ok", service = "SecurePrintManager", version = 1 });

            if (request.Operation == "authenticate")
                return Authenticate(request, db);

            if (!TryGetSession(request, out var session))
                return Error(request, "UNAUTHORIZED", "Authentication is required.");

            return request.Operation switch
            {
                "get_status" => await GetStatusAsync(request, db, ct),
                "get_jobs" => await GetJobsAsync(request, db, session, ct),
                "get_users" when session.IsAdmin => await GetUsersAsync(request, db, ct),
                "get_audit" when session.IsAdmin => await GetAuditAsync(request, db, ct),
                "get_config" when session.IsAdmin => await GetConfigAsync(request, db, ct),
                _ => Error(request, "FORBIDDEN", "The operation is not permitted for this session.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPC operation {Operation} failed", request.Operation);
            return Error(request, "INTERNAL_ERROR", "The service could not complete the operation.");
        }
    }

    private ResponseEnvelope Authenticate(RequestEnvelope request, DatabaseContext db)
    {
        if (request.Payload.ValueKind != JsonValueKind.Object)
            return Error(request, "INVALID_PAYLOAD", "Authentication payload is invalid.");

        var mode = request.Payload.TryGetProperty("mode", out var modeProperty) ? modeProperty.GetString() : null;
        var auth = new AuthenticationService(db);
        AuthResult result;

        switch (mode)
        {
            case "password":
                result = auth.AuthenticateByUsernamePassword(
                    request.Payload.GetProperty("username").GetString() ?? string.Empty,
                    request.Payload.GetProperty("password").GetString() ?? string.Empty);
                break;
            case "pin":
                result = auth.AuthenticateByPin(request.Payload.GetProperty("pin").GetString() ?? string.Empty);
                break;
            case "card":
                result = auth.AuthenticateByCard(request.Payload.GetProperty("card").GetString() ?? string.Empty);
                break;
            default:
                return Error(request, "INVALID_AUTH_MODE", "Authentication mode must be password, pin or card.");
        }

        if (!result.Success || result.User == null)
            return Error(request, "AUTH_FAILED", "Authentication failed.");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var timeout = 15;
        var config = db.Configs.FirstOrDefault(c => c.Key == "SessionTimeoutMinutes");
        if (config != null && int.TryParse(config.Value, out var configured) && configured is >= 1 and <= 240)
            timeout = configured;

        _sessions[token] = new Session(result.User.Id, result.User.IsAdmin, DateTime.UtcNow.AddMinutes(timeout));
        return Ok(request, new { token, userId = result.User.Id, username = result.User.Username, isAdmin = result.User.IsAdmin, expiresUtc = _sessions[token].ExpiresUtc });
    }

    private bool TryGetSession(RequestEnvelope request, out Session session)
    {
        session = default!;
        if (request.Payload.ValueKind != JsonValueKind.Object || !request.Payload.TryGetProperty("sessionToken", out var tokenProperty))
            return false;
        var token = tokenProperty.GetString();
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!_sessions.TryGetValue(token, out session)) return false;
        if (session.ExpiresUtc <= DateTime.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        return true;
    }

    private void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _sessions)
            if (pair.Value.ExpiresUtc <= now)
                _sessions.TryRemove(pair.Key, out _);
    }

    private static Task<ResponseEnvelope> GetStatusAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct) =>
        Task.FromResult(Ok(request, new
        {
            status = "running",
            users = db.Users.Count(u => u.IsActive),
            pendingJobs = db.PrintJobs.Count(j => j.Status == "HOLD"),
            timestampUtc = DateTime.UtcNow
        }));

    private static async Task<ResponseEnvelope> GetJobsAsync(RequestEnvelope request, DatabaseContext db, Session session, CancellationToken ct)
    {
        var limit = 100;
        var filterUserId = (int?)null;
        if (request.Payload.ValueKind == JsonValueKind.Object)
        {
            if (request.Payload.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n))
                limit = Math.Clamp(n, 1, 500);
            if (!session.IsAdmin) filterUserId = session.UserId;
            else if (request.Payload.TryGetProperty("userId", out var uid) && uid.TryGetInt32(out var requestedUser))
                filterUserId = requestedUser;
        }
        else if (!session.IsAdmin)
        {
            filterUserId = session.UserId;
        }

        var query = db.PrintJobs.AsNoTracking().OrderByDescending(j => j.Timestamp).AsQueryable();
        if (filterUserId.HasValue) query = query.Where(j => j.UserId == filterUserId.Value);

        var jobs = await query.Take(limit)
            .Select(j => new { j.Id, j.UserId, j.DocumentName, j.Pages, j.PrinterName, j.Color, j.Duplex, j.Status, j.Timestamp, j.PrintedAt, j.ReleasedBy, j.Cost })
            .ToListAsync(ct);
        return Ok(request, jobs);
    }

    private static async Task<ResponseEnvelope> GetUsersAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.FullName, u.Department, u.MonthlyQuota, u.PagesUsed, u.ScanQuota, u.ScansUsed, u.IsActive, u.IsAdmin, u.LastLogin })
            .ToListAsync(ct);
        return Ok(request, users);
    }

    private static async Task<ResponseEnvelope> GetAuditAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct)
    {
        var logs = await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(500)
            .Select(a => new { a.Id, a.Action, a.Username, a.DocumentName, a.Details, a.Timestamp, a.PreviousHash, a.CurrentHash })
            .ToListAsync(ct);
        return Ok(request, logs);
    }

    private static async Task<ResponseEnvelope> GetConfigAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct)
    {
        var config = await db.Configs.AsNoTracking().OrderBy(c => c.Key).ToDictionaryAsync(c => c.Key, c => c.Value, ct);
        return Ok(request, config);
    }

    private static ResponseEnvelope Ok(RequestEnvelope request, object payload) =>
        new(PrintManagerProtocol.CurrentVersion, request.RequestId, true, null, null, JsonSerializer.SerializeToElement(payload));

    private static ResponseEnvelope Error(RequestEnvelope request, string code, string message) =>
        new(PrintManagerProtocol.CurrentVersion, request.RequestId, false, code, message, null);

    private sealed record Session(int UserId, bool IsAdmin, DateTime ExpiresUtc);
}
