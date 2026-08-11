using System.Text.Json;

namespace SecurePrintManager.Core.Ipc;

public static class PrintManagerProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxMessageBytes = 64 * 1024;
    public const string PipeName = "SecurePrintManager.Control.v1";

    public static byte[] Serialize(RequestEnvelope request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions.Default);

    public static RequestEnvelope DeserializeRequest(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<RequestEnvelope>(payload, JsonOptions.Default)
        ?? throw new InvalidDataException("IPC request is empty or invalid.");

    public static byte[] Serialize(ResponseEnvelope response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions.Default);

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            MaxDepth = 16
        };
    }
}

public sealed record RequestEnvelope(
    int Version,
    string RequestId,
    string Operation,
    JsonElement Payload);

public sealed record ResponseEnvelope(
    int Version,
    string RequestId,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? Payload);
