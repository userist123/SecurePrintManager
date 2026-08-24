using System.IO.Pipes;
using SecurePrintManager.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace SecurePrintManager.Service.Ipc;

/// <summary>
/// Legacy compatibility wrapper. The hosted IPC service is the canonical server.
/// </summary>
public sealed class PrintManagerPipeServer(ILogger<PrintManagerPipeServer> logger)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Legacy PrintManagerPipeServer is disabled; PrintManagerPipeHostedService owns the canonical IPC endpoint.");
        await Task.CompletedTask;
    }
}
