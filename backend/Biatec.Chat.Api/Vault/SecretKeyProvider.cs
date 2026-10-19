using System.Security.Cryptography;

namespace Biatec.Chat.Api.Vault;

/// <summary>
/// Production key provider without a cloud KMS: a 32-byte master key from <c>Vault:MasterKey</c> (base64), held in a
/// Kubernetes secret that only the API pods mount, wraps each data key with AES-GCM. The deploy generates the key once
/// (docs/cicd.md); losing it makes every stored token unreadable, which only forces users to sign in again.
/// </summary>
public sealed class SecretKeyProvider : IKeyProvider
{
    private static readonly byte[] Aad = "biatec-vault-secret-wrap"u8.ToArray();
    private readonly byte[] _master;

    public SecretKeyProvider(byte[] masterKey)
    {
        if (masterKey.Length != 32) throw new ArgumentException("Vault:MasterKey must be base64 of 32 bytes.", nameof(masterKey));
        _master = masterKey;
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
