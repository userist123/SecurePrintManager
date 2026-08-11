using SecurePrintManager.Core.Ipc;
using System.Text.Json;

namespace SecurePrintManager.Tests;

public sealed class IpcProtocolTests
{
    [Fact]
    public void Request_round_trips()
    {
        var request = new RequestEnvelope(
            1,
            "abc",
            "health",
            JsonSerializer.SerializeToElement(new { value = 1 }));

        var decoded = PrintManagerProtocol.DeserializeRequest(
            PrintManagerProtocol.Serialize(request));

        Assert.Equal(request.Version, decoded.Version);
        Assert.Equal(request.RequestId, decoded.RequestId);
        Assert.Equal(request.Operation, decoded.Operation);
    }

    [Fact]
    public async Task Frame_round_trips()
    {
        await using var stream = new MemoryStream();
        var payload = new byte[] { 1, 2, 3, 4 };

        await NamedPipeFrame.WriteAsync(stream, payload, default);
        stream.Position = 0;

        var result = await NamedPipeFrame.ReadAsync(stream, default);

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task Oversized_frame_is_rejected()
    {
        await using var stream = new MemoryStream();
        var payload = new byte[PrintManagerProtocol.MaxMessageBytes + 1];

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NamedPipeFrame.WriteAsync(stream, payload, default));
    }
}
