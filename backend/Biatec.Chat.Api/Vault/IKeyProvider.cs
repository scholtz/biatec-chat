namespace Biatec.Chat.Api.Vault;

/// <summary>A fresh data key: the plaintext is used once for AES-GCM and zeroed, the wrapped form is stored with the ciphertext.</summary>
public sealed record DataKey(byte[] Plaintext, byte[] Wrapped);

/// <summary>Envelope-encryption key source. Implementations: AWS KMS (production) and a local dev key.</summary>
public interface IKeyProvider
{
    Task<DataKey> GenerateDataKeyAsync(CancellationToken ct);

    /// <summary>Unwraps a data key produced by <see cref="GenerateDataKeyAsync"/>. The caller zeroes the result.</summary>
    Task<byte[]> UnwrapDataKeyAsync(byte[] wrapped, CancellationToken ct);
}
