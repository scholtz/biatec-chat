using System.Security.Cryptography;
using System.Text.Json;

namespace Biatec.Chat.Core.Evidence;

/// <summary>A public evidence-signing key as published at the JWKS endpoint (ES256, P-256).</summary>
public sealed record EvidencePublicKey(string Kid, byte[] X, byte[] Y, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);

/// <summary>Source of the evidence signing keys. The private key never leaves the implementation.</summary>
public interface IEvidenceKeyStore
{
    /// <summary>Kid of the key that signs new entries.</summary>
    Task<string> GetActiveKidAsync(CancellationToken ct);

    /// <summary>Signs with the key named by kid (ES256, IEEE P1363 r||s).</summary>
    Task<byte[]> SignAsync(string kid, byte[] data, CancellationToken ct);

    /// <summary>Every key ever used, active and retired, for the JWKS endpoint.</summary>
    Task<IReadOnlyList<EvidencePublicKey>> GetPublicKeysAsync(CancellationToken ct);

    /// <summary>Retires the active key (stamping its retirement date; it stays published) and activates a new one.</summary>
    Task<string> RotateAsync(CancellationToken ct);
}

/// <summary>
/// Local development key store, like the vault's DevKeyProvider: keys live in process memory, or in a JSON file when
/// <c>filePath</c> is set so they survive restarts. Production deployments replace it with a store backed by
/// Secrets Manager or KMS. Never use it in production.
/// </summary>
public sealed class DevEvidenceKeyStore : IEvidenceKeyStore, IDisposable
{
    private sealed class Held
    {
        public required string Kid { get; init; }
        public required ECDsa Key { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? RetiredAt { get; set; }
    }

    private sealed record FileKey(string Kid, string Pkcs8, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);

    private readonly object _gate = new();
    private readonly List<Held> _keys = [];
    private readonly string? _filePath;
    private readonly TimeProvider _time;

    public DevEvidenceKeyStore(string? filePath = null, TimeProvider? time = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        _time = time ?? TimeProvider.System;
        if (_filePath is not null && File.Exists(_filePath))
        {
            foreach (var k in JsonSerializer.Deserialize<List<FileKey>>(File.ReadAllText(_filePath)) ?? [])
            {
                var ec = ECDsa.Create();
                ec.ImportPkcs8PrivateKey(Convert.FromBase64String(k.Pkcs8), out _);
                _keys.Add(new Held { Kid = k.Kid, Key = ec, CreatedAt = k.CreatedAt, RetiredAt = k.RetiredAt });
            }
        }
        if (_keys.Count(k => k.RetiredAt is null) > 1)
            throw new InvalidOperationException("The evidence key file holds more than one active key.");
        if (!_keys.Any(k => k.RetiredAt is null))
        {
            _keys.Add(NewKey());
            Persist();
        }
    }

    private Held NewKey() => new()
    {
        Kid = "ev-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256),
        CreatedAt = _time.GetUtcNow(),
    };

    private void Persist()
    {
        if (_filePath is null) return;
        var json = JsonSerializer.Serialize(_keys.Select(k =>
            new FileKey(k.Kid, Convert.ToBase64String(k.Key.ExportPkcs8PrivateKey()), k.CreatedAt, k.RetiredAt)));
        var tmp = _filePath + ".tmp";
        var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, opts))
        {
            fs.Write(System.Text.Encoding.UTF8.GetBytes(json));
        }
        File.Move(tmp, _filePath, overwrite: true);
    }

    public void Dispose()
    {
        lock (_gate) foreach (var k in _keys) k.Key.Dispose();
    }

    public Task<string> GetActiveKidAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_keys.Single(k => k.RetiredAt is null).Kid);
    }

    public Task<byte[]> SignAsync(string kid, byte[] data, CancellationToken ct)
    {
        Held k;
        lock (_gate) k = _keys.FirstOrDefault(x => x.Kid == kid) ?? throw new InvalidOperationException($"Unknown evidence key '{kid}'.");
        return Task.FromResult(k.Key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public Task<IReadOnlyList<EvidencePublicKey>> GetPublicKeysAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<EvidencePublicKey> list = _keys.Select(k =>
            {
                var p = k.Key.ExportParameters(false);
                return new EvidencePublicKey(k.Kid, p.Q.X!, p.Q.Y!, k.CreatedAt, k.RetiredAt);
            }).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<string> RotateAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var old = _keys.Single(k => k.RetiredAt is null);
            var fresh = NewKey();
            old.RetiredAt = _time.GetUtcNow();
            _keys.Add(fresh);
            try { Persist(); }
            catch
            {
                // Keep memory and file consistent: a key that is not on disk must not sign anything.
                _keys.Remove(fresh);
                old.RetiredAt = null;
                fresh.Key.Dispose();
                throw;
            }
            return Task.FromResult(fresh.Kid);
        }
    }
}
