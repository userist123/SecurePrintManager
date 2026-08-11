using System.IO.Pipes;
using System.Text.Json;

namespace SecurePrintManager.Core.Ipc;

public sealed class PrintManagerClient
{
    public async Task<ResponseEnvelope> SendAsync(
        string operation,
        object payload,
        CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            PrintManagerProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(3000, ct);

        var payloadJson = JsonSerializer.SerializeToElement(payload);
        var request = new RequestEnvelope(
            PrintManagerProtocol.CurrentVersion,
            Guid.NewGuid().ToString("N"),
            operation,
            payloadJson);

        await NamedPipeFrame.WriteAsync(
            pipe,
            PrintManagerProtocol.Serialize(request),
            ct);

        return PrintManagerProtocol.DeserializeResponse(
            await NamedPipeFrame.ReadAsync(pipe, ct));
    }
}
