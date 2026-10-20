namespace Biatec.Chat.Core.Data;

/// <summary>
/// Public metadata of an evidence-signing key, shared by every replica so the JWKS is identical everywhere and keeps
/// retired keys across restarts. Holds NO private key material: that lives in the secret or in KMS.
/// </summary>
public class EvidenceSigningKey
{
    public string Kid { get; set; } = "";
    public byte[] X { get; set; } = [];
    public byte[] Y { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    /// <summary>True for the single key that signs new entries (enforced by a partial unique index).</summary>
    public bool IsActive { get; set; }
}
