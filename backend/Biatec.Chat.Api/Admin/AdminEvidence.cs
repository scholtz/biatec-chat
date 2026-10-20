using System.Text.Json.Nodes;
using Biatec.Chat.Api.Onboarding;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;

namespace Biatec.Chat.Api.Admin;

/// <summary>
/// The admin evidence chain: every committed admin change (operator tenant approval, tenant policy, knowledge-base upload, delete and reload) is
/// appended to one signed, hash-chained evidence chain with the fixed id <see cref="ChainId"/>, using the same
/// <see cref="IEvidenceLog"/>, encodings and signing keys (JWKS) as conversation chains. The id is not a conversation:
/// no conversation row has it, so no session can read it; operators read it at <c>GET /onboarding/operator/evidence</c>.
/// A payload holds the operator name, action, target, reason and before/after values: never a key, secret or page
/// content (a page is recorded by its SHA-256, size and type).
/// </summary>
public static class AdminEvidence
{
    /// <summary>The admin chain's id. Conversation ids are random version-4 GUIDs, so they never take this value.</summary>
    public static readonly Guid ChainId = Guid.Parse("00000000-0000-0000-0000-00000000ad01");

    public const string Kind = "admin_change";

    public const string TenantPolicyUpdate = "tenant_policy_update";
    public const string KbPageUpload = "kb_page_upload";
    public const string KbPageDelete = "kb_page_delete";
    public const string KbReload = "kb_reload";
    public const string TenantApprove = "tenant_approve";

    /// <summary>
    /// Appends one admin change. Call it inside the change's own transaction where there is one, so the change commits
    /// only together with its evidence (a failing append rolls the change back).
    /// </summary>
    public static Task<EvidenceEntry> AppendAsync(IEvidenceLog log, OperatorIdentity op, string action, string target, string? reason,
        JsonObject? before, JsonObject? after, CancellationToken ct) =>
        log.AppendAsync(ChainId, Kind, new JsonObject
        {
            ["operator"] = op.Name,
            ["operatorAuth"] = op.Legacy ? "legacy-shared-key" : "named-key",
            ["action"] = action,
            ["target"] = target,
            ["reason"] = reason,
            ["before"] = before,
            ["after"] = after,
        }, ct: ct);
}
