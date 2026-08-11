using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecurePrintManager.Core;

public interface IAuthorizationService
{
    Task<bool> AuthorizeAsync(Principal principal, Permission permission, CancellationToken ct);
}

public interface IQuotaManager
{
    Task ReserveAsync(Guid userId, int pages, Guid jobId, CancellationToken ct);
    Task ReleaseAsync(Guid userId, int pages, Guid jobId, CancellationToken ct);
}

public interface IDocumentProtector
{
    Task<EncryptedDocument> ProtectAsync(Stream plaintext, CancellationToken ct);
    Task<Stream> UnprotectAsync(EncryptedDocument document, CancellationToken ct);
}

public sealed record EncryptedDocument(byte[] Ciphertext, byte[] Nonce, byte[] Tag);

public sealed class AesGcmDocumentProtector(byte[] key) : IDocumentProtector
{
    private readonly byte[] _key = key is { Length: 32 } ? key.ToArray() : throw new ArgumentException("AES-256 requires 32 bytes.");
    public async Task<EncryptedDocument> ProtectAsync(Stream plaintext, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await plaintext.CopyToAsync(ms, ct);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[ms.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, ms.ToArray(), cipher, tag);
        return new(cipher, nonce, tag);
    }
    public Task<Stream> UnprotectAsync(EncryptedDocument d, CancellationToken ct)
    {
        var plain = new byte[d.Ciphertext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(d.Nonce, d.Ciphertext, d.Tag, plain);
        return Task.FromResult<Stream>(new MemoryStream(plain, false));
    }
}

public sealed record AuditEvent(Guid Id, DateTimeOffset Timestamp, string Actor, string Action, string Resource, string Result, string PreviousHash, string Hash);

public sealed class HashChainAuditLogger
{
    private string _lastHash = "GENESIS";
    public AuditEvent Append(string actor, string action, string resource, string result)
    {
        var id = Guid.NewGuid(); var ts = DateTimeOffset.UtcNow;
        var canonical = JsonSerializer.Serialize(new { id, ts, actor, action, resource, result, previous = _lastHash });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var e = new AuditEvent(id, ts, actor, action, resource, result, _lastHash, hash);
        _lastHash = hash; return e;
    }
}
