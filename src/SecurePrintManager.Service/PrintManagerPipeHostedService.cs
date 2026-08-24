using System.IO.Pipes;
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

            return request.Operation switch
            {
                "health" => Ok(request, new { status = "ok", service = "SecurePrintManager", version = 1 }),
                "get_status" => await GetStatusAsync(request, db, ct),
                "get_jobs" => await GetJobsAsync(request, db, ct),
                "get_users" => await GetUsersAsync(request, db, ct),
                "get_audit" => await GetAuditAsync(request, db, ct),
                "get_config" => await GetConfigAsync(request, db, ct),
                _ => Error(request, "UNKNOWN_OPERATION", $"Operation '{request.Operation}' is not supported.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPC operation {Operation} failed", request.Operation);
            return Error(request, "INTERNAL_ERROR", "The service could not complete the operation.");
        }
    }

    private static Task<ResponseEnvelope> GetStatusAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct) =>
        Task.FromResult(Ok(request, new
        {
            status = "running",
            users = db.Users.Count(u => u.IsActive),
            pendingJobs = db.PrintJobs.Count(j => j.Status == "HOLD"),
            timestampUtc = DateTime.UtcNow
        }));

    private static async Task<ResponseEnvelope> GetJobsAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct)
    {
        var payload = request.Payload.ValueKind == JsonValueKind.Object
            ? request.Payload
            : default;
        var limit = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, 500)
            : 100;

        var jobs = await db.PrintJobs.AsNoTracking()
            .OrderByDescending(j => j.Timestamp)
            .Take(limit)
            .Select(j => new
            {
                j.Id,
                j.UserId,
                j.DocumentName,
                j.Pages,
                j.PrinterName,
                j.Color,
                j.Duplex,
                j.Status,
                j.Timestamp,
                j.PrintedAt,
                j.ReleasedBy,
                j.Cost
            })
            .ToListAsync(ct);

        return Ok(request, jobs);
    }

    private static async Task<ResponseEnvelope> GetUsersAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.FullName,
                u.Department,
                u.MonthlyQuota,
                u.PagesUsed,
                u.ScanQuota,
                u.ScansUsed,
                u.IsActive,
                u.IsAdmin,
                u.LastLogin
            })
            .ToListAsync(ct);

        return Ok(request, users);
    }

    private static async Task<ResponseEnvelope> GetAuditAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct)
    {
        var logs = await db.AuditLogs.AsNoTracking()
            .OrderByDescending(a => a.Id)
            .Take(500)
            .Select(a => new
            {
                a.Id,
                a.Action,
                a.Username,
                a.DocumentName,
                a.Details,
                a.Timestamp,
                a.PreviousHash,
                a.CurrentHash
            })
            .ToListAsync(ct);

        return Ok(request, logs);
    }

    private static async Task<ResponseEnvelope> GetConfigAsync(RequestEnvelope request, DatabaseContext db, CancellationToken ct)
    {
        var config = await db.Configs.AsNoTracking()
            .OrderBy(c => c.Key)
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);
        return Ok(request, config);
    }

    private static ResponseEnvelope Ok(RequestEnvelope request, object payload)
    {
        var element = JsonSerializer.SerializeToElement(payload);
        return new ResponseEnvelope(PrintManagerProtocol.CurrentVersion, request.RequestId, true, null, null, element);
    }

    private static ResponseEnvelope Error(RequestEnvelope request, string code, string message) =>
        new(PrintManagerProtocol.CurrentVersion, request.RequestId, false, code, message, null);
}
