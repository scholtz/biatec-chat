using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Biatec.Chat.Api.Config;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Vault;

/// <summary>AWS KMS envelope encryption: GenerateDataKey and Decrypt in eu-central-1 using the default credential chain.</summary>
public sealed class KmsKeyProvider : IKeyProvider
{
    private static readonly Dictionary<string, string> Context = new() { ["purpose"] = "biatec-chat-token-vault" };
    private readonly IAmazonKeyManagementService _kms;
    private readonly string _keyId;

    public KmsKeyProvider(IOptions<KmsOptions> options)
        : this(new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(options.Value.Region)), options.Value.KeyId) { }

    public KmsKeyProvider(IAmazonKeyManagementService kms, string keyId)
    {
        _kms = kms;
        _keyId = keyId;
    }

    public async Task<DataKey> GenerateDataKeyAsync(CancellationToken ct)
    {
        var r = await _kms.GenerateDataKeyAsync(new GenerateDataKeyRequest
        {
            KeyId = _keyId, KeySpec = DataKeySpec.AES_256, EncryptionContext = Context,
        }, ct);
        return new DataKey(r.Plaintext.ToArray(), r.CiphertextBlob.ToArray());
    }

    public async Task<byte[]> UnwrapDataKeyAsync(byte[] wrapped, CancellationToken ct)
    {
        using var ms = new MemoryStream(wrapped);
        var r = await _kms.DecryptAsync(new DecryptRequest { KeyId = _keyId, CiphertextBlob = ms, EncryptionContext = Context }, ct);
        return r.Plaintext.ToArray();
    }
}
