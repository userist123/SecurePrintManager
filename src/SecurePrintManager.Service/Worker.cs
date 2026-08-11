using SecurePrintManager.Service.Ipc;

namespace SecurePrintManager.Service;

public sealed class Worker(
    ILogger<Worker> logger,
    PrintManagerPipeServer pipeServer) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SecurePrintManager Service started.");
        await pipeServer.RunAsync(stoppingToken);
    }
}
