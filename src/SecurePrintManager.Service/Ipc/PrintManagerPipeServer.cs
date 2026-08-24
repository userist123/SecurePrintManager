using System.IO.Pipes;
using System.Text.Json;
using SecurePrintManager.Core;
using SecurePrintManager.Core.Ipc;
using SecurePrintManager.Database;

namespace SecurePrintManager.Service.Ipc;

/// <summary>
/// Server named-pipe pentru comenzi de la UI: release/cancel pe un job aflat în HOLD.
/// Anterior toate operațiile erau NOT_IMPLEMENTED și acest server nici măcar nu era
/// pornit de Worker - MainWindow marca job-ul "PRINTED" direct în DB, fără să spună
/// vreodată spooler-ului să reia tipărirea. Asta implementează calea reală.
/// </summary>
public sealed class PrintManagerPipeServer(
    ILogger<PrintManagerPipeServer> logger,
    DatabaseContext db,
    AuditLogger audit,
    QuotaManager quota)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                PrintManagerProtocol.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);

                var request = PrintManagerProtocol.DeserializeRequest(
                    await NamedPipeFrame.ReadAsync(pipe, stoppingToken));

                var response = await HandleAsync(request, stoppingToken);

                await NamedPipeFrame.WriteAsync(
                    pipe,
                    PrintManagerProtocol.Serialize(response),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SecurePrintManager IPC request failed.");
            }
        }
    }

    private async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        if (request.Version != PrintManagerProtocol.CurrentVersion)
            return Fail(request, "UNSUPPORTED_VERSION", "Unsupported IPC protocol version.");

        return request.Operation switch
        {
            "ReleaseJob" => await HandleReleaseJobAsync(request, ct),
            "CancelJob" => await HandleCancelJobAsync(request, ct),
            _ => Fail(request, "NOT_IMPLEMENTED", $"Operation '{request.Operation}' is not implemented yet.")
        };
    }

    private async Task<ResponseEnvelope> HandleReleaseJobAsync(RequestEnvelope request, CancellationToken ct)
    {
        JobActionRequest? payload;
        try
        {
            payload = request.Payload.Deserialize<JobActionRequest>();
        }
        catch (JsonException)
        {
            return Fail(request, "BAD_PAYLOAD", "Payload invalid pentru ReleaseJob.");
        }

        if (payload is null)
            return Fail(request, "BAD_PAYLOAD", "Payload invalid pentru ReleaseJob.");

        var job = await db.PrintJobs.FindAsync(new object?[] { payload.PrintJobId }, ct);
        if (job is null)
            return Fail(request, "NOT_FOUND", $"Job {payload.PrintJobId} nu exista.");

        if (job.Status != "HOLD")
            return Fail(request, "INVALID_STATE", $"Job {payload.PrintJobId} este in starea '{job.Status}', nu HOLD.");

        if (job.SpoolerJobId is null)
            return Fail(request, "NO_SPOOLER_JOB",
                "Jobul nu are un ID de spooler asociat (interceptarea a esuat sau jobul e dintr-o versiune anterioara).");

        try
        {
            SpoolerControl.Resume(job.PrinterName, job.SpoolerJobId.Value);
        }
        catch (Exception ex)
        {
            audit.Log("ERROR", payload.ActingUser, job.DocumentName, $"Resume esuat pentru job {job.Id}: {ex.Message}");
            return Fail(request, "SPOOLER_ERROR", ex.Message);
        }

        job.Status = "PRINTED";
        job.PrintedAt = DateTime.Now;
        job.ReleasedBy = payload.ActingUser;
        quota.UseQuota(job.UserId, job.Pages);
        await db.SaveChangesAsync(ct);

        audit.Log("RELEASE", payload.ActingUser, job.DocumentName, $"Job {job.Id} eliberat catre spooler. Pagini: {job.Pages}");

        return Success(request, new JobActionResponse(true, "Job eliberat catre imprimanta."));
    }

    private async Task<ResponseEnvelope> HandleCancelJobAsync(RequestEnvelope request, CancellationToken ct)
    {
        JobActionRequest? payload;
        try
        {
            payload = request.Payload.Deserialize<JobActionRequest>();
        }
        catch (JsonException)
        {
            return Fail(request, "BAD_PAYLOAD", "Payload invalid pentru CancelJob.");
        }

        if (payload is null)
            return Fail(request, "BAD_PAYLOAD", "Payload invalid pentru CancelJob.");

        var job = await db.PrintJobs.FindAsync(new object?[] { payload.PrintJobId }, ct);
        if (job is null)
            return Fail(request, "NOT_FOUND", $"Job {payload.PrintJobId} nu exista.");

        if (job.Status == "HOLD" && job.SpoolerJobId is not null)
        {
            try
            {
                SpoolerControl.Cancel(job.PrinterName, job.SpoolerJobId.Value);
            }
            catch (Exception ex)
            {
                audit.Log("ERROR", payload.ActingUser, job.DocumentName, $"Cancel esuat pentru job {job.Id}: {ex.Message}");
                // continuăm oricum să-l marcăm DELETED în DB - nu vrem un job "fantomă"
                // blocat in UI doar pentru că anularea la spooler a eșuat.
            }
        }

        job.Status = "DELETED";
        await db.SaveChangesAsync(ct);
        audit.Log("DELETE", payload.ActingUser, job.DocumentName, $"Job {job.Id} anulat de utilizator.");

        return Success(request, new JobActionResponse(true, "Job anulat."));
    }

    private static ResponseEnvelope Fail(RequestEnvelope request, string code, string message) =>
        new(PrintManagerProtocol.CurrentVersion, request.RequestId, false, code, message, null);

    private static ResponseEnvelope Success<T>(RequestEnvelope request, T payload) =>
        new(PrintManagerProtocol.CurrentVersion, request.RequestId, true, null, null,
            JsonSerializer.SerializeToElement(payload));
}
