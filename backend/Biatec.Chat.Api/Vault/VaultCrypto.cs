using System.Security.Cryptography;
using System.Text;

namespace Biatec.Chat.Api.Vault;

/// <summary>
/// Envelope format: version(1) | wrappedKeyLen(2, BE) | wrappedKey | nonce(12) | tag(16) | ciphertext.
/// Every seal uses a new data key and a new random nonce, so a (key, nonce) pair is never reused. The session id is
/// authenticated as associated data, so a blob copied to another session's key fails to open.
/// </summary>
public static class VaultCrypto
{
    private const byte Version = 1;
    private const int NonceSize = 12, TagSize = 16;

    public static async Task<byte[]> SealAsync(IKeyProvider keys, string sessionId, byte[] plaintext, CancellationToken ct)
    {
        var dk = await keys.GenerateDataKeyAsync(ct);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var gcm = new AesGcm(dk.Plaintext, TagSize)) gcm.Encrypt(nonce, plaintext, cipher, tag, Aad(sessionId));

            var output = new byte[3 + dk.Wrapped.Length + NonceSize + TagSize + cipher.Length];
            output[0] = Version;
            output[1] = (byte)(dk.Wrapped.Length >> 8);
            output[2] = (byte)dk.Wrapped.Length;
            var o = 3;
            dk.Wrapped.CopyTo(output, o); o += dk.Wrapped.Length;
            nonce.CopyTo(output, o); o += NonceSize;
            tag.CopyTo(output, o); o += TagSize;
            cipher.CopyTo(output, o);
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(dk.Plaintext); }
    }

    public static async Task<byte[]> OpenAsync(IKeyProvider keys, string sessionId, byte[] blob, CancellationToken ct)
    {
        if (blob.Length < 3 + NonceSize + TagSize || blob[0] != Version) throw new CryptographicException("Unrecognised vault entry format.");
        var wrappedLen = (blob[1] << 8) | blob[2];
        var o = 3;
        if (blob.Length < o + wrappedLen + NonceSize + TagSize) throw new CryptographicException("Truncated vault entry.");
        var wrapped = blob.AsSpan(o, wrappedLen).ToArray(); o += wrappedLen;
        var key = await keys.UnwrapDataKeyAsync(wrapped, ct);
        try
        {
            var nonce = blob.AsSpan(o, NonceSize); o += NonceSize;
            var tag = blob.AsSpan(o, TagSize); o += TagSize;
            var cipher = blob.AsSpan(o);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plain, Aad(sessionId));
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] Aad(string sessionId) => Encoding.UTF8.GetBytes("biatec-vault-v1|" + sessionId);
}
