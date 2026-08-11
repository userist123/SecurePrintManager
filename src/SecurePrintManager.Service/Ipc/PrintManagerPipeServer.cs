using System.IO.Pipes;
using SecurePrintManager.Core.Ipc;

namespace SecurePrintManager.Service.Ipc;

public sealed class PrintManagerPipeServer(ILogger<PrintManagerPipeServer> logger)
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

    private static Task<ResponseEnvelope> HandleAsync(
        RequestEnvelope request,
        CancellationToken ct)
    {
        if (request.Version != PrintManagerProtocol.CurrentVersion)
            return Task.FromResult(new ResponseEnvelope(
                PrintManagerProtocol.CurrentVersion,
                request.RequestId,
                false,
                "UNSUPPORTED_VERSION",
                "Unsupported IPC protocol version.",
                null));

        return Task.FromResult(new ResponseEnvelope(
            PrintManagerProtocol.CurrentVersion,
            request.RequestId,
            false,
            "NOT_IMPLEMENTED",
            $"Operation '{request.Operation}' is not implemented yet.",
            null));
    }
}
