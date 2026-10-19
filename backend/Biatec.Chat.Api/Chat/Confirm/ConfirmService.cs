using ActionPreviewStatus = Biatec.Chat.Core.Data.ActionPreviewStatus;
using ActionPreview = Biatec.Chat.Core.Data.ActionPreview;
using ActionStep = Biatec.Chat.Core.Data.ActionStep;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Chat.Confirm;

public enum ConfirmOutcome
{
    /// <summary>On chain; the step's txIds are recorded.</summary>
    Landed,
    /// <summary>Broadcast, but not yet seen confirmed within the wait.</summary>
    Submitted,
    /// <summary>Biatec OIDC asked for authenticator approval; completion is T-049.</summary>
    ConsentPending,
    /// <summary>Biatec OIDC (or the token refresh in front of it) is unreachable. Tier and vault are untouched.</summary>
    SignInUnavailable,
    /// <summary>The session's token lacks <c>sign</c>; the widget offers "Authorize signing".</summary>
    ScopeRequired,
    /// <summary>The user has to sign in again (no usable tokens).</summary>
    SignInRequired,
    /// <summary>Signing or submission failed for good; the preview must be rebuilt.</summary>
    Failed,
}

public sealed record ConfirmResult(
    ConfirmOutcome Outcome,
    string Message,
    IReadOnlyList<string> TxIds,
    Contracts.ConsentStatus? Consent = null,
    string? ReauthorizeScope = null,
    string? ErrorCode = null);

/// <summary>
/// The confirm path every fund-moving capability shares: the user's Confirm, then the scope check, then the
/// exact previewed bytes to Biatec OIDC <c>POST /wallet/{network}/{address}/sign</c> (never Biatec MCP's
/// signTransaction), verification of what comes back against the previewed group, submission through Biatec MCP and
/// confirmation tracking.
/// </summary>
public sealed class ConfirmService(
    ChatDbContext db,
    ActionPreviewService previews,
    IScopeGuard scopes,
    ITokenVault vault,
    IWalletSigningClient signer,
    IBiatecMcpClient mcp,
    ITransactionConfirmer confirmer,
    IEvidenceLog evidence,
    TimeProvider clock,
    ILogger<ConfirmService> log)
{
    public const string OutageMessage = "The sign-in service is unavailable. Your session is unchanged; please try again shortly.";

    public async Task<ConfirmResult> ConfirmAsync(Tenant tenant, ChatSession session, Guid previewId, string groupHash, CancellationToken ct)
    {
        var sessionId = session.Id.ToString("N");

        // Peek first: scope and group integrity are checked before the preview is flipped to Confirmed, so a refusal
        // here leaves it awaiting confirmation.
        var peek = await previews.GetAsync(session.ConversationId, previewId, session.AbsoluteExpiresAt, ct);
        var pending = peek.Steps.FirstOrDefault(s => s.Status == StepStatus.Pending);
        if (pending is null || pending.Kind != ActionStepKind.Sign || peek.Preview.Status != ActionPreviewStatus.Proposed)
            throw new PreviewException(PreviewError.InvalidState, "The preview has no signing step awaiting confirmation.");

        var scope = await scopes.CheckAsync(sessionId, ElevationScopes.Sign, ct);
        if (!scope.Allowed)
        {
            return scope.Reauthorize is null
                ? new ConfirmResult(ConfirmOutcome.SignInRequired, scope.Message ?? "Sign in to continue.", [], ErrorCode: "sign_in_required")
                : new ConfirmResult(ConfirmOutcome.ScopeRequired, scope.Message ?? "Signing needs your permission.", [], ReauthorizeScope: scope.Reauthorize, ErrorCode: "scope_required");
        }

        IReadOnlyList<Algorand.Algod.Model.Transactions.Transaction> group;
        string address;
        try { (group, address) = PrepareGroup(pending); }
        catch (SigningVerificationException ex)
        {
            await FailAsync(peek.Preview, pending, ct);
            await RecordAsync(session, "signing_refused", new JsonObject { ["previewId"] = previewId.ToString(), ["stepId"] = pending.Id.ToString(), ["reason"] = "group_mismatch" }, ct);
            return new ConfirmResult(ConfirmOutcome.Failed, ex.Message + " Nothing was signed; the preview must be rebuilt.", [], ErrorCode: "group_mismatch");
        }

        // The user's Confirm: hash, expiry and the 600 second gate are enforced here (throws PreviewException).
        var snapshot = await previews.ConfirmAsync(session.ConversationId, previewId, groupHash, session.AbsoluteExpiresAt, ct);
        var preview = snapshot.Preview;
        var step = snapshot.Steps.First(s => s.Id == pending.Id);

        string? bearer;
        try { bearer = await vault.GetAccessTokenAsync(sessionId, ct); }
        catch (VaultRefreshException)
        {
            return await OutageAsync(session, preview, step, "token_refresh", ct);
        }
        if (bearer is null)
        {
            await RevertAsync(preview, ct);
            return new ConfirmResult(ConfirmOutcome.SignInRequired, "Sign in to continue.", [], ErrorCode: "sign_in_required");
        }

        var remaining = (int)Math.Max(1, (step.ExpiresAt - clock.GetUtcNow()).TotalSeconds);
        var origin = new Uri(tenant.RedirectUri).GetLeftPart(UriPartial.Authority);
        var outcome = await signer.SignAsync(bearer, new WalletSignRequest(step.Network!, address, step.UnsignedGroupList(), Convert.FromHexString(step.SubjectHash),
            RequireConsent: false, MaxConsentSeconds: remaining, Origin: origin), ct);

        switch (outcome)
        {
            case WalletSignOutcome.Unavailable u:
                return await OutageAsync(session, preview, step, u.Reason, ct);

            case WalletSignOutcome.StepUp s:
                return await ConsentPendingAsync(session, preview, step, s, ct);

            case WalletSignOutcome.Rejected r:
                await FailAsync(preview, step, ct);
                await RecordAsync(session, "signing_refused", new JsonObject
                {
                    ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(), ["status"] = r.Status, ["error"] = r.Error,
                }, ct);
                return new ConfirmResult(ConfirmOutcome.Failed, RejectionMessage(r), [], ErrorCode: r.Error);

            case WalletSignOutcome.Signed signed:
                return await SubmitAsync(tenant, session, preview, step, group, signed, ct);

            default:
                throw new InvalidOperationException("Unknown signing outcome.");
        }
    }

    private (IReadOnlyList<Algorand.Algod.Model.Transactions.Transaction> Group, string Address) PrepareGroup(ActionStep step)
    {
        if (string.IsNullOrEmpty(step.UnsignedGroup) || string.IsNullOrEmpty(step.SenderAddress) || string.IsNullOrEmpty(step.Network))
            throw new SigningVerificationException("The step has no stored group, sender or network.");
        if (!step.Network.StartsWith("algorand", StringComparison.OrdinalIgnoreCase))
            throw new SigningVerificationException("Only Algorand groups can be confirmed here.");
        var group = SignedGroupVerifier.VerifyUnsigned(step.UnsignedGroupList(), step.SubjectHash);
        // One sender per signing call.
        if (group.Any(t => !string.Equals(t.Sender.EncodeAsString(), step.SenderAddress, StringComparison.Ordinal)))
            throw new SigningVerificationException("The group has more than one sender.");
        return (group, step.SenderAddress);
    }

    private async Task<ConfirmResult> SubmitAsync(Tenant tenant, ChatSession session, ActionPreview preview, ActionStep step,
        IReadOnlyList<Algorand.Algod.Model.Transactions.Transaction> group, WalletSignOutcome.Signed signed, CancellationToken ct)
    {
        // Binding check: what Biatec OIDC returned must be the previewed group, byte for byte.
        IReadOnlyList<byte[]> raw;
        try { raw = SignedGroupVerifier.VerifySigned(signed.SignedTransactions, group, step.SubjectHash); }
        catch (SigningVerificationException ex)
        {
            await FailAsync(preview, step, ct);
            await RecordAsync(session, "signing_refused", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["reason"] = "signed_group_mismatch" }, ct);
            return new ConfirmResult(ConfirmOutcome.Failed, ex.Message + " Nothing was submitted.", [], ErrorCode: "signed_group_mismatch");
        }

        var txIds = Biatec.Chat.Tools.Algod.AlgorandGroup.Ensure(group).TxIds.ToArray();
        step.Status = StepStatus.Signed;
        await db.SaveChangesAsync(ct);

        var sessionId = session.Id.ToString("N");
        var entry = await vault.GetAsync(sessionId, ct);
        var ctx = new McpCallContext(tenant.Id, sessionId, entry?.Scopes ?? []);
        McpToolResult? result = null;
        string? submitError = null;
        try { result = await mcp.SubmitTransactionToBlockchainAsync(ctx, step.Network!, SignedGroupVerifier.ToSubmissionBlob(raw), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            submitError = ex.GetType().Name;
        }
        if (submitError is not null || result is null || result.IsError)
        {
            await FailAsync(preview, step, ct);
            await RecordAsync(session, "submission_failed", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash }, ct);
            return new ConfirmResult(ConfirmOutcome.Failed, "The signed transactions could not be submitted. Nothing moved; the preview must be rebuilt.", [], ErrorCode: "submit_failed");
        }

        step.TxIds = txIds;
        step.Status = StepStatus.Submitted;
        preview.Status = ActionPreviewStatus.InProgress;
        await db.SaveChangesAsync(ct);
        await RecordAsync(session, "action_submitted", new JsonObject
        {
            ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["seq"] = step.Seq,
            ["groupHash"] = step.SubjectHash, ["txIds"] = new JsonArray(txIds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["valuedEur"] = signed.ValuedEur,
        }, ct);

        // A group lands atomically, so following its first transaction is enough.
        var tracked = await confirmer.WaitAsync(step.Network!, txIds[0], ct);
        switch (tracked.State)
        {
            case TxConfirmationState.Confirmed:
                await previews.MarkStepLandedAsync(session.ConversationId, preview.Id, step.Seq, txIds, IsFinalStep(preview, step), ct);
                await RecordAsync(session, "action_receipt", new JsonObject
                {
                    ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["seq"] = step.Seq,
                    ["groupHash"] = step.SubjectHash, ["confirmedRound"] = tracked.Round.ToString(),
                    ["txIds"] = new JsonArray(txIds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                }, ct);
                return new ConfirmResult(ConfirmOutcome.Landed, "The transaction is confirmed on chain.", txIds);
            case TxConfirmationState.Rejected:
                await FailAsync(preview, step, ct);
                await RecordAsync(session, "submission_failed", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash, ["reason"] = "network_rejected" }, ct);
                return new ConfirmResult(ConfirmOutcome.Failed, "The network rejected the transaction.", txIds, ErrorCode: "network_rejected");
            default:
                return new ConfirmResult(ConfirmOutcome.Submitted, "The transaction was submitted and is waiting to be confirmed.", txIds);
        }
    }

    /// <summary>
    /// A step is the last one unless the preview's canonical JSON announces more in an integer <c>totalSteps</c>
    /// (multi-step actions build later steps only after the earlier one landed).
    /// </summary>
    private static bool IsFinalStep(ActionPreview preview, ActionStep step)
    {
        try
        {
            using var doc = JsonDocument.Parse(preview.CanonicalJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("totalSteps", out var t) && t.TryGetInt32(out var total))
                return step.Seq + 1 >= total;
        }
        catch (JsonException) { }
        return true;
    }

    private async Task<ConfirmResult> ConsentPendingAsync(ChatSession session, ActionPreview preview, ActionStep step, WalletSignOutcome.StepUp s, CancellationToken ct)
    {
        var expires = clock.GetUtcNow().AddSeconds(s.ExpiresInSeconds);
        step.Status = StepStatus.AwaitingConsent;
        db.ConsentRequests.Add(new ConsentRequest
        {
            Id = Guid.NewGuid(), StepId = step.Id, SubjectHash = step.SubjectHash, AuthReqId = s.AuthReqId,
            BindingCode = s.BindingCode, ExpiresAt = expires, Status = Biatec.Chat.Core.Data.ConsentStatus.Pending,
        });
        await db.SaveChangesAsync(ct);
        await RecordAsync(session, "consent_requested", new JsonObject
        {
            ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash,
            ["expiresAt"] = expires.ToString("O"),
        }, ct);
        return new ConfirmResult(ConfirmOutcome.ConsentPending, "Waiting for approval on your phone.", [],
            Consent: new Contracts.ConsentStatus
            {
                PreviewId = preview.Id.ToString(), StepId = step.Id.ToString(), State = ConsentState.Pending,
                BindingCode = s.BindingCode, ExpiresAt = expires,
            });
    }

    /// <summary>
    /// Biatec OIDC is unreachable: the session keeps its tier and vault entry, the preview goes back to awaiting
    /// confirmation so the user can retry, and the outage is recorded as an evidence event.
    /// </summary>
    private async Task<ConfirmResult> OutageAsync(ChatSession session, ActionPreview preview, ActionStep step, string reason, CancellationToken ct)
    {
        await RevertAsync(preview, ct);
        await RecordAsync(session, "oidc_outage", new JsonObject
        {
            ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["reason"] = reason,
        }, ct);
        return new ConfirmResult(ConfirmOutcome.SignInUnavailable, OutageMessage, [], ErrorCode: "sign_in_unavailable");
    }

    private async Task RevertAsync(ActionPreview preview, CancellationToken ct)
    {
        if (preview.Status != ActionPreviewStatus.Confirmed) return;
        preview.Status = ActionPreviewStatus.Proposed;
        await db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(ActionPreview preview, ActionStep step, CancellationToken ct)
    {
        step.Status = StepStatus.Failed;
        preview.Status = ActionPreviewStatus.Failed;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Evidence must never turn an already-signed action into an error; a failed append is logged by type only.</summary>
    private async Task RecordAsync(ChatSession session, string kind, JsonObject payload, CancellationToken ct)
    {
        try { await evidence.AppendAsync(session.ConversationId, kind, payload, ct: ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Could not record evidence {Kind}: {ErrorType}", kind, ex.GetType().Name);
        }
    }

    private static string RejectionMessage(WalletSignOutcome.Rejected r) => r.Error switch
    {
        "daily_cap_exceeded" => "This would exceed your spending limit.",
        "access_denied" => "The approval was denied.",
        "invalid_token" => "Your sign-in is no longer valid. Please sign in again.",
        _ => "Signing was refused. Nothing moved; the preview must be rebuilt.",
    };
}

internal static class ActionStepExtensions
{
    /// <summary>The step's unsigned group (JSON array of base64 msgpack).</summary>
    public static IReadOnlyList<string> UnsignedGroupList(this ActionStep step) =>
        Parse(step.UnsignedGroup);

    private static IReadOnlyList<string> Parse(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? []; }
        catch (JsonException) { throw new SigningVerificationException("The stored group is not readable."); }
    }
}
