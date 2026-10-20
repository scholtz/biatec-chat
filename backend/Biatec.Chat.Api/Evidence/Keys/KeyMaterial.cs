using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;

namespace Biatec.Chat.Api.Evidence.Keys;

/// <summary>A newly provisioned signing key: its kid and public point. The private part stays in the material source.</summary>
public sealed record NewEvidenceKey(string Kid, byte[] X, byte[] Y);

/// <summary>Where the private signing keys live (a secret or KMS). Public metadata is kept in PostgreSQL by <see cref="DbEvidenceKeyStore"/>.</summary>
public interface IEvidenceKeyMaterial
{
    /// <summary>Provisions the next key; <paramref name="usedKids"/> are the kids already recorded.</summary>
    Task<NewEvidenceKey> CreateAsync(IReadOnlySet<string> usedKids, CancellationToken ct);

    /// <summary>ES256 signature (IEEE P1363 r||s) with the private key of <paramref name="kid"/>.</summary>
    Task<byte[]> SignAsync(string kid, byte[] data, CancellationToken ct);
}

/// <summary>
/// Every key is derived with HKDF-SHA256 from one master secret (Kubernetes secret, or Secrets Manager injected as
/// env), so all replicas can sign with every kid while the database and logs only ever see public points.
/// Rotation picks a fresh random kid; the master secret never changes. Rotating the master itself would orphan old kids.
/// </summary>
public sealed class SecretEvidenceKeyMaterial : IEvidenceKeyMaterial, IDisposable
{
    private readonly byte[] _master;
    private readonly ConcurrentDictionary<string, ECDsa> _keys = new();

    public SecretEvidenceKeyMaterial(byte[] masterKey)
    {
        if (masterKey.Length < 32) throw new InvalidOperationException("Evidence:Secret:MasterKey must be at least 32 bytes (base64).");
        _master = masterKey;
    }

    public static SecretEvidenceKeyMaterial FromBase64(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) throw new InvalidOperationException("Evidence:KeyStore=Secret requires Evidence:Secret:MasterKey (base64, at least 32 bytes).");
        try { return new SecretEvidenceKeyMaterial(Convert.FromBase64String(b64)); }
        catch (FormatException) { throw new InvalidOperationException("Evidence:Secret:MasterKey is not valid base64."); }
    }

    private ECParameters Derive(string kid)
    {
        var bytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, _master, 48, salt: Encoding.UTF8.GetBytes("biatec-chat/evidence-signing/v1"), info: Encoding.UTF8.GetBytes(kid));
        return P256.ToParameters(P256.ScalarFromBytes(bytes));
    }

    private ECDsa Key(string kid) => _keys.GetOrAdd(kid, k => ECDsa.Create(Derive(k)));

    public Task<NewEvidenceKey> CreateAsync(IReadOnlySet<string> usedKids, CancellationToken ct)
    {
        var kid = "ev-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var p = Derive(kid);
        return Task.FromResult(new NewEvidenceKey(kid, p.Q.X!, p.Q.Y!));
    }

    public Task<byte[]> SignAsync(string kid, byte[] data, CancellationToken ct) =>
        Task.FromResult(Key(kid).SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose()
    {
        foreach (var k in _keys.Values) k.Dispose();
        CryptographicOperations.ZeroMemory(_master);
    }
}

/// <summary>
/// AWS KMS asymmetric keys (ECC_NIST_P256, SIGN_VERIFY) that operators created beforehand and listed in
/// <c>Evidence:Kms:KeyIds</c> (this code never creates AWS resources). Rotation activates the next listed, unused key.
/// The pod role needs kms:Sign and kms:GetPublicKey on them.
/// </summary>
public sealed class KmsEvidenceKeyMaterial : IEvidenceKeyMaterial
{
    private readonly IAmazonKeyManagementService _kms;
    private readonly Dictionary<string, string> _keyIdByKid;

    public KmsEvidenceKeyMaterial(IAmazonKeyManagementService kms, IEnumerable<string> keyIds)
    {
        _kms = kms;
        _keyIdByKid = keyIds.ToDictionary(KidFor, k => k);
        if (_keyIdByKid.Count == 0) throw new InvalidOperationException("Evidence:KeyStore=Kms requires at least one Evidence:Kms:KeyIds entry.");
    }

    public static KmsEvidenceKeyMaterial Create(string region, IEnumerable<string> keyIds) =>
        new(new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(region)), keyIds);

    public static string KidFor(string keyId) => "ev-kms-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyId)))[..16];

    public async Task<NewEvidenceKey> CreateAsync(IReadOnlySet<string> usedKids, CancellationToken ct)
    {
        var next = _keyIdByKid.FirstOrDefault(p => !usedKids.Contains(p.Key));
        if (next.Key is null) throw new InvalidOperationException("Every configured Evidence:Kms:KeyIds key has been used; add a new KMS key id to rotate.");
        var r = await _kms.GetPublicKeyAsync(new GetPublicKeyRequest { KeyId = next.Value }, ct);
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(r.PublicKey.ToArray(), out _);
        var q = ec.ExportParameters(false).Q;
        return new NewEvidenceKey(next.Key, q.X!, q.Y!);
    }

    public async Task<byte[]> SignAsync(string kid, byte[] data, CancellationToken ct)
    {
        if (!_keyIdByKid.TryGetValue(kid, out var keyId)) throw new InvalidOperationException($"Evidence key '{kid}' is not in Evidence:Kms:KeyIds.");
        using var ms = new MemoryStream(data);
        var r = await _kms.SignAsync(new SignRequest { KeyId = keyId, Message = ms, MessageType = MessageType.RAW, SigningAlgorithm = SigningAlgorithmSpec.ECDSA_SHA_256 }, ct);
        return DerToP1363(r.Signature.ToArray());
    }

    public static byte[] DerToP1363(byte[] der)
    {
        var seq = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        var o = new byte[64];
        Put(seq.ReadInteger(), o.AsSpan(0, 32));
        Put(seq.ReadInteger(), o.AsSpan(32, 32));
        return o;
    }

    private static void Put(BigInteger v, Span<byte> dest)
    {
        var b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        b.CopyTo(dest[(32 - b.Length)..]);
    }
}
