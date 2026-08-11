namespace SecurePrintManager.Core.Ipc;

public static class NamedPipeFrame
{
    public const int HeaderSize = 4;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > PrintManagerProtocol.MaxMessageBytes)
            throw new InvalidDataException("IPC payload exceeds the configured limit.");

        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await ReadExactlyAsync(stream, header, ct);
        var length = BitConverter.ToInt32(header, 0);

        if (length <= 0 || length > PrintManagerProtocol.MaxMessageBytes)
            throw new InvalidDataException("Invalid IPC frame length.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, ct);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], ct);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
