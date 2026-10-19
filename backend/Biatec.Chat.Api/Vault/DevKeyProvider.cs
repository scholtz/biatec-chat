using System.Security.Cryptography;

namespace Biatec.Chat.Api.Vault;

/// <summary>
/// Local key provider that needs no AWS. A master key from <c>Vault:DevKey</c> (base64, 32 bytes) wraps the data keys;
/// without one a random key lives only in this process, so entries do not survive a restart. Never use in production.
/// </summary>
public sealed class DevKeyProvider : IKeyProvider
{
    private static readonly byte[] Aad = "biatec-vault-dev-wrap"u8.ToArray();
    private readonly byte[] _master;

    public DevKeyProvider(byte[]? masterKey = null)
    {
        _master = masterKey ?? RandomNumberGenerator.GetBytes(32);
        if (_master.Length != 32) throw new ArgumentException("The dev key must be 32 bytes (base64 of 32 bytes).", nameof(masterKey));
    }

    public Task<DataKey> GenerateDataKeyAsync(CancellationToken ct)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[32];
        var tag = new byte[16];
        using (var gcm = new AesGcm(_master, 16)) gcm.Encrypt(nonce, key, cipher, tag, Aad);
        return Task.FromResult(new DataKey(key, [.. nonce, .. tag, .. cipher]));
    }

    public Task<byte[]> UnwrapDataKeyAsync(byte[] wrapped, CancellationToken ct)
    {
        if (wrapped.Length != 12 + 16 + 32) throw new CryptographicException("Malformed wrapped data key.");
        var key = new byte[32];
        using var gcm = new AesGcm(_master, 16);
        gcm.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28, 32), wrapped.AsSpan(12, 16), key, Aad);
        return Task.FromResult(key);
    }
}
