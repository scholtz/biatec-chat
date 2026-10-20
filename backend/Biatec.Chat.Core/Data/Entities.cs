namespace Biatec.Chat.Core.Data;

// Entities for the "Data Model (Backend)" table of PRODUCT-DESCRIPTION.md. TokenVaultEntry lives in Redis, not here.
// Hashes and signatures are stored as text (hex or base64url as the producer defines); JSON columns are jsonb.

public enum SessionTier { Anonymous, Authenticated }
/// <summary>What the assistant may do for the user: analyze (read only) or prepare (build previews the user confirms).</summary>
public enum AgentMode { Analyze, Prepare }
public enum MessageRole { User, Assistant, Tool, System }
public enum ActionPreviewStatus { Proposed, Confirmed, InProgress, Completed, Failed, Cancelled, Expired }
public enum ActionStepKind { Sign, Limits }
public enum StepStatus { Pending, AwaitingConsent, Signed, Submitted, Confirmed, Failed, Expired, Cancelled }
public enum ConsentStatus { Pending, Approved, Denied, Expired, Cancelled }
public enum PopupAuthPurpose { Login, Elevate }
public enum PopupAuthStatus { Pending, Completed, Expired, Failed }
public enum SafeApprovalKind { Approve, Execute }
public enum SoftCapStatus { Reserved, Final }

public class Conversation
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string? OidcSub { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Set by the user's erasure request (T-060). An erased conversation is never treated as anonymous.</summary>
    public DateTimeOffset? ErasedAt { get; set; }
}

public class ChatSession
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public SessionTier Tier { get; set; }
    public string TenantId { get; set; } = "";
    public string? OidcSub { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    /// <summary>Set only by the user through PUT /session/agent. Per session: a new sign-in resets it to Prepare.</summary>
    public AgentMode AgentMode { get; set; } = AgentMode.Prepare;
    public bool Paused { get; set; }
}

public class Message
{
    public Guid ConversationId { get; set; }
    public long Seq { get; set; }
    public MessageRole Role { get; set; }
    /// <summary>Stored after redaction.</summary>
    public string Content { get; set; } = "";
    public string? ToolCalls { get; set; }
    public string? ToolResults { get; set; }
    public string? ModelId { get; set; }
    public string? Usage { get; set; }
    /// <summary>When the row was written; the payload retention window (5 years by default) clears each message by its own age (T-060).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

public class ActionPreview
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Capability { get; set; } = "";
    public string CanonicalJson { get; set; } = "";
    public decimal? PredictedValueEur { get; set; }
    public bool PredictedNeedsConsent { get; set; }
    /// <summary>Earliest step expiry.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    public ActionPreviewStatus Status { get; set; }
    public Guid? RebuiltAsPreviewId { get; set; }
}

public class ActionStep
{
    public Guid Id { get; set; }
    public Guid PreviewId { get; set; }
    public int Seq { get; set; }
    public ActionStepKind Kind { get; set; }
    public string SubjectHash { get; set; } = "";
    /// <summary>Sign steps only.</summary>
    public string? UnsignedGroup { get; set; }
    /// <summary>Limits steps only.</summary>
    public string? LimitsRequest { get; set; }
    public string? SenderAddress { get; set; }
    public string? Network { get; set; }
    public long? FirstValid { get; set; }
    public long? LastValid { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public StepStatus Status { get; set; }
    public string[] TxIds { get; set; } = [];
}

public class ConsentRequest
{
    public Guid Id { get; set; }
    public Guid StepId { get; set; }
    public string SubjectHash { get; set; } = "";
    public string AuthReqId { get; set; } = "";
    public string? BindingCode { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public ConsentStatus Status { get; set; }
    /// <summary>Stored verbatim.</summary>
    public string? Receipt { get; set; }
    /// <summary>
    /// The canonical, registered page origin the consent was requested under (reported to Biatec OIDC again on the resume).
    /// Null on rows written before T-079: the resume reports the tenant's backend origin then.
    /// </summary>
    public string? Origin { get; set; }
}

/// <summary>Append-only. Payload and Salt become null after deletion; the hashes and signature stay.</summary>
public class EvidenceEntry
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public long Seq { get; set; }
    public string Kind { get; set; } = "";
    public string? AnchorRef { get; set; }
    public string? Payload { get; set; }
    public string? Salt { get; set; }
    public string PayloadHash { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string EntryHash { get; set; } = "";
    public string Kid { get; set; } = "";
    public DateTimeOffset SignedAt { get; set; }
    /// <summary>Over entryHash, kid and signedAt.</summary>
    public string Signature { get; set; } = "";
}

public class EvidenceAnchor
{
    public Guid Id { get; set; }
    public long SequenceNumber { get; set; }
    public string? PreviousAnchorTxId { get; set; }
    public string AnchorTxId { get; set; } = "";
    public string MerkleRoot { get; set; } = "";
    public DateTimeOffset AnchoredAt { get; set; }
    /// <summary>Marker naming lost sequence numbers, if any.</summary>
    public string? GapFrom { get; set; }
}

/// <summary>
/// Durable record of an anchor attempt: written before the transaction is broadcast, so the sequence number stays
/// consumed across restarts and failures. <c>pending</c> until the anchor row is stored (then the attempt row is
/// deleted); <c>lost</c> when the transaction could not be confirmed (the next anchor records the gap).
/// </summary>
public class EvidenceAnchorAttempt
{
    public long SequenceNumber { get; set; }
    /// <summary>"pending" or "lost".</summary>
    public string Status { get; set; } = "pending";
    public string MerkleRoot { get; set; } = "";
    public string? PreviousAnchorTxId { get; set; }
    public string? GapFrom { get; set; }
    /// <summary>JSON array of the leaves (conversationId, anchoredSeq, headHash) so the proofs can be rebuilt after a crash.</summary>
    public string Leaves { get; set; } = "[]";
    /// <summary>Set once the transaction was confirmed on chain, before the anchor row is stored.</summary>
    public string? TxId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class EvidenceAnchorLeaf
{
    public Guid AnchorId { get; set; }
    public Guid ConversationId { get; set; }
    public long AnchoredSeq { get; set; }
    public string HeadHash { get; set; } = "";
    public int LeafIndex { get; set; }
    /// <summary>JSON array of {hash, side} where side is left or right.</summary>
    public string SiblingHashes { get; set; } = "[]";
}

public class PublicConversationState
{
    public Guid ConversationId { get; set; }
    public string CapabilityHash { get; set; } = "";
    public string ContinuationTokenHash { get; set; } = "";
    public string? PreviousTokenHash { get; set; }
    public string? LastReplyRef { get; set; }
    public DateTimeOffset? AdoptedAt { get; set; }
}

public class PopupAuthRequest
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public PopupAuthPurpose Purpose { get; set; }
    public string NonceHash { get; set; } = "";
    public PopupAuthStatus Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public class PendingRevocation
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public byte[] EncryptedRefreshToken { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
}

public class Tenant
{
    public string Id { get; set; } = "";
    public string Host { get; set; } = "";
    public string? SignInPageUrl { get; set; }
    public string BedrockBudgets { get; set; } = "{}";
    public string SafeApprovalCaps { get; set; } = "{}";
    public decimal ConsentThresholdCeilingEur { get; set; }
    public bool LimitsUpdateEnabled { get; set; }
    public bool X402Enabled { get; set; }
    public string OidcClientId { get; set; } = "";
    /// <summary>Pointer into the secret store, never the secret.</summary>
    public string ClientSecretRef { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string[] AllowedOrigins { get; set; } = [];
    public string ClientKeys { get; set; } = "[]";
    public decimal SoftCapEurPerHour { get; set; }
    public bool TrustAnchorVerified { get; set; }
    public string? DefaultModel { get; set; }
    public string Suggestions { get; set; } = "[]";
}

public class SafeApprovalCount
{
    public Guid Id { get; set; }
    public string OidcSub { get; set; } = "";
    public string TenantId { get; set; } = "";
    public SafeApprovalKind Kind { get; set; }
    /// <summary>The valuedEur of the approved payload; zero for executions.</summary>
    public decimal ValueEur { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
}

public class SoftCapSpend
{
    public Guid Id { get; set; }
    public string OidcSub { get; set; } = "";
    public string TenantId { get; set; } = "";
    public SoftCapStatus Status { get; set; }
    public DateTimeOffset ReservedAt { get; set; }
    public DateTimeOffset? SpentAt { get; set; }
    public decimal AmountEur { get; set; }
    /// <summary>Where AmountEur came from: oidc (valuedEur returned with the signature) or predicted.</summary>
    public string ValueSource { get; set; } = "predicted";
}

/// <summary>
/// A Biatec Safe deployed through this tenant's chat (<c>safe.create</c>). Public on-chain data only, no user
/// identifier: <c>safe.list</c> asks the chain which of these the signed-in user is a member of.
/// </summary>
public class KnownSafe
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    /// <summary>"mainnet" or "testnet".</summary>
    public string Network { get; set; } = "";
    public long AppId { get; set; }
    /// <summary>The deployment transaction, so a later call resolves the app id without asking the chain again.</summary>
    public string? DeployTxId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A knowledge-base page uploaded by an operator through the admin API (T-096). Every replica serves the same rows
/// (overlaying the seed pages under <c>Kb:Path</c>). The body is untrusted data: it reaches the model only as a
/// <c>kb.search</c> tool result, never the system prompt.
/// </summary>
public class KbStoredPage
{
    /// <summary>Lower-case slug; what the model cites as the source id.</summary>
    public string Id { get; set; } = "";
    /// <summary>"text/markdown" or "text/plain".</summary>
    public string ContentType { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>UTF-8 size of <see cref="Body"/> in bytes.</summary>
    public int SizeBytes { get; set; }
    /// <summary>Lower-case hex SHA-256 of the UTF-8 body; replicas compare it to see whether to reload.</summary>
    public string Sha256 { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
