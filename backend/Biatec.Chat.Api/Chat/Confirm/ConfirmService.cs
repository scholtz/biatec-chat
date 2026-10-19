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
    /// <summary>The user set the agent to analyze only or paused it; nothing was touched and the preview stays valid.</summary>
    AgentRestricted,
}

public sealed record ConfirmResult(
    ConfirmOutcome Outcome,
    string Message,
    IReadOnlyList<string> TxIds,
    Contracts.ConsentStatus? Consent = null,
    string? ReauthorizeScope = null,
    string? ErrorCode = null,
    string? ResourceContent = null);

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
    ILogger<ConfirmService> log,
    IEnumerable<ISettlementHandler>? settlementHandlers = null,
    Biatec.Chat.Api.Limits.LimitsConfirmService? limits = null,
    IEvidenceRecorder? recorder = null)
{
    public const string OutageMessage = "The sign-in service is unavailable. Your session is unchanged; please try again shortly.";

    public async Task<ConfirmResult> ConfirmAsync(Tenant tenant, ChatSession session, Guid previewId, string groupHash, CancellationToken ct)
    {
        var sessionId = session.Id.ToString("N");

        // Agent mode and pause are enforced here, server-side, before anything is read or flipped.
        if (Biatec.Chat.Api.Agent.AgentModePolicy.ConfirmRefusal(session) is { } refusal)
            return new ConfirmResult(ConfirmOutcome.AgentRestricted, refusal.Message, [], ErrorCode: refusal.Code);

        // Peek first: scope and group integrity are checked before the preview is flipped to Confirmed, so a refusal
        // here leaves it awaiting confirmation.
        var peek = await previews.GetAsync(session.ConversationId, previewId, session.AbsoluteExpiresAt, ct);
        var pending = peek.Steps.FirstOrDefault(s => s.Status == StepStatus.Pending);
        // A limits step (lowering only) goes to Biatec OIDC PUT /wallet/limits, not to the signing endpoint.
        if (pending is { Kind: ActionStepKind.Limits } && limits is not null && peek.Preview.Status == ActionPreviewStatus.Proposed)
            return await limits.ConfirmAsync(tenant, session, previewId, groupHash, ct);
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
        await RecordExtraAsync(session, EvidenceKinds.UserConfirmed, new JsonObject
        {
            ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(), ["seq"] = step.Seq, ["groupHash"] = step.SubjectHash,
            ["capability"] = preview.Capability,
        }, ct);

        try
        {
            return await SignAndSubmitAsync(tenant, session, preview, step, group, address, sessionId, previewId, ct);
        }
        catch (Exception ex)
        {
            // The preview is Confirmed here. If nothing was handed on yet (the step is still Pending), an unexpected
            // error or an aborted request must not leave it stuck: put it back to awaiting confirmation. A cancelled
            // request may already have reached Biatec OIDC, so the abort is recorded and the revert ignores the token.
            if (step.Status == StepStatus.Pending)
            {
                try
                {
                    await RevertAsync(preview, CancellationToken.None);
                    await RecordAsync(session, "confirm_aborted", new JsonObject
                    {
                        ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(),
                        ["reason"] = ex is OperationCanceledException ? "cancelled" : ex.GetType().Name,
                    }, CancellationToken.None);
                }
                catch (Exception revertEx)
                {
                    log.LogError("Could not revert preview after a failed confirm: {ErrorType}", revertEx.GetType().Name);
                }
            }
            throw;
        }
    }

    private async Task<ConfirmResult> SignAndSubmitAsync(Tenant tenant, ChatSession session, ActionPreview preview, ActionStep step,
        IReadOnlyList<Algorand.Algod.Model.Transactions.Transaction> group, string address, string sessionId, Guid previewId, CancellationToken ct)
    {
        // Fail closed BEFORE anything is signed: x402.pay needs the tenant flag (it may have been turned off after the
        // preview was made) and its settlement handler (platform flag); the MCP broadcast would pay for nothing.
        if (string.Equals(preview.Capability, "x402.pay", StringComparison.Ordinal))
        {
            string? refusal = null, message = null;
            if (!tenant.X402Enabled) { refusal = "feature_disabled"; message = "x402 payments are disabled for this workspace. Nothing was signed; the preview must be rebuilt."; }
            else if (settlementHandlers?.Any(h => string.Equals(h.Capability, preview.Capability, StringComparison.Ordinal)) != true)
            { refusal = "settlement_unavailable"; message = "x402 payments are not available right now. Nothing was signed; the preview must be rebuilt."; }
            if (refusal is not null)
            {
                await FailAsync(preview, step, CancellationToken.None);
                await RecordAsync(session, "submission_failed", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash, ["reason"] = refusal }, CancellationToken.None);
                return new ConfirmResult(ConfirmOutcome.Failed, message!, [], ErrorCode: refusal);
            }
        }

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
        await RecordExtraAsync(session, EvidenceKinds.SigningRequested, new JsonObject
        {
            ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash,
            ["network"] = step.Network, ["address"] = address, ["transactionCount"] = step.UnsignedGroupList().Count,
            ["maxConsentSeconds"] = remaining, ["origin"] = origin,
        }, ct);
        var outcome = await signer.SignAsync(bearer, new WalletSignRequest(step.Network!, address, step.UnsignedGroupList(), Convert.FromHexString(step.SubjectHash),
            RequireConsent: false, MaxConsentSeconds: remaining, Origin: origin), ct);

        await RecordExtraAsync(session, EvidenceKinds.SigningOutcome, SigningOutcomePayload(previewId, step, outcome), CancellationToken.None);

        switch (outcome)
        {
            case WalletSignOutcome.Unavailable u:
                return await OutageAsync(session, preview, step, u.Reason, ct);

            case WalletSignOutcome.StepUp s:
                return await ConsentPendingAsync(session, preview, step, s, ct);

            case WalletSignOutcome.Rejected { Error: "invalid_token" }:
                // An expired or invalid sign-in says nothing about the group: keep the preview confirmable after re-auth.
                await RevertAsync(preview, ct);
                await RecordAsync(session, "signing_refused", new JsonObject
                {
                    ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(), ["error"] = "invalid_token",
                }, ct);
                return new ConfirmResult(ConfirmOutcome.SignInRequired, "Your sign-in is no longer valid. Please sign in again.", [], ErrorCode: "sign_in_required");

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
        // Record "submitted, unconfirmed" BEFORE handing the group to MCP and ignore the request's cancellation: once
        // the signed bytes leave, the group may be broadcast even if this request is aborted, and Reconcile can only
        // resolve a Submitted step that has TxIds.
        step.TxIds = txIds;
        step.Status = StepStatus.Submitted;
        preview.Status = ActionPreviewStatus.InProgress;
        await db.SaveChangesAsync(CancellationToken.None);

        var sessionId = session.Id.ToString("N");
        var handler = settlementHandlers?.FirstOrDefault(h => string.Equals(h.Capability, preview.Capability, StringComparison.Ordinal));
        if (handler is not null)
            return await SettleViaHandlerAsync(handler, session, preview, step, raw, txIds, signed, ct);
        var entry = await vault.GetAsync(sessionId, ct);
        var ctx = new McpCallContext(tenant.Id, sessionId, entry?.Scopes ?? []);
        McpToolResult? result = null;
        string? unknownError = null;
        try { result = await mcp.SubmitTransactionToBlockchainAsync(ctx, step.Network!, SignedGroupVerifier.ToSubmissionBlob(raw), ct); }
        catch (Exception ex)
        {
            // A timeout, network error or abort after the request left may still have been broadcast: the outcome is
            // unknown. The step stays Submitted so Reconcile resolves it.
            unknownError = ex is OperationCanceledException ? "cancelled" : ex.GetType().Name;
        }
        if (unknownError is null && (result is null || result.IsError))
        {
            // An explicit MCP error result: the group was not accepted.
            step.TxIds = [];
            await FailAsync(preview, step, CancellationToken.None);
            await RecordAsync(session, "submission_failed", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash }, ct);
            return new ConfirmResult(ConfirmOutcome.Failed, "The signed transactions could not be submitted. Nothing moved; the preview must be rebuilt.", [], ErrorCode: "submit_failed");
        }

        if (unknownError is not null)
            await RecordAsync(session, "submission_unknown", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash, ["error"] = unknownError }, CancellationToken.None);
        await RecordAsync(session, "action_submitted", new JsonObject
        {
            ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["seq"] = step.Seq,
            ["groupHash"] = step.SubjectHash, ["txIds"] = new JsonArray(txIds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["valuedEur"] = signed.ValuedEur,
        }, CancellationToken.None);

        if (unknownError == "cancelled") throw new OperationCanceledException(ct);

        return await TrackAsync(session, preview, step, txIds, unknownError is not null, ct);
    }

    /// <summary>
    /// Capabilities that settle the signed group themselves (x402) instead of the MCP broadcast. The step is recorded as
    /// submitted first, exactly as for the MCP path, so an abort or an unknown outcome can be reconciled on chain.
    /// </summary>
    private async Task<ConfirmResult> SettleViaHandlerAsync(ISettlementHandler handler, ChatSession session, ActionPreview preview, ActionStep step,
        IReadOnlyList<byte[]> raw, string[] txIds, WalletSignOutcome.Signed signed, CancellationToken ct)
    {
        await RecordAsync(session, "action_submitted", new JsonObject
        {
            ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["seq"] = step.Seq, ["groupHash"] = step.SubjectHash,
            ["txIds"] = new JsonArray(txIds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()), ["valuedEur"] = signed.ValuedEur,
            ["settledBy"] = handler.Capability,
        }, CancellationToken.None);

        SettlementResult result;
        try { result = await handler.SettleAsync(preview, step, raw.Select(Convert.ToBase64String).ToArray(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new SettlementResult(SettlementKind.Unknown, "The payment could not be confirmed either way; it may still settle. Check again shortly.", ErrorCode: ex.GetType().Name);
        }

        switch (result.Kind)
        {
            case SettlementKind.NotBroadcast:
                step.TxIds = [];
                await FailAsync(preview, step, CancellationToken.None);
                await RecordAsync(session, "submission_failed", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash, ["reason"] = result.ErrorCode ?? "settlement_refused" }, CancellationToken.None);
                return new ConfirmResult(ConfirmOutcome.Failed, result.Message, [], ErrorCode: result.ErrorCode ?? "submit_failed");
            case SettlementKind.Settled:
                await previews.MarkStepLandedAsync(session.ConversationId, preview.Id, step.Seq, txIds, IsFinalStep(preview, step), CancellationToken.None);
                await RecordAsync(session, "action_receipt", new JsonObject
                {
                    ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["seq"] = step.Seq, ["groupHash"] = step.SubjectHash,
                    ["txIds"] = new JsonArray(txIds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                    ["settledBy"] = handler.Capability, ["delivered"] = result.Content is not null,
                }, CancellationToken.None);
                return new ConfirmResult(ConfirmOutcome.Landed, result.Message, txIds, ErrorCode: result.ErrorCode, ResourceContent: result.Content);
            default:
                await RecordAsync(session, "submission_unknown", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash, ["error"] = result.ErrorCode }, CancellationToken.None);
                return await TrackAsync(session, preview, step, txIds, true, ct);
        }
    }

    /// <summary>
    /// Lazy reconciliation: a step left Submitted (the wait timed out, or the submit outcome was unknown) is checked
    /// on chain again; it moves to Landed with an <c>action_receipt</c>, or to Failed if the network rejected it.
    /// </summary>
    public async Task<ConfirmResult> ReconcileAsync(ChatSession session, Guid previewId, CancellationToken ct)
    {
        var snap = await previews.GetAsync(session.ConversationId, previewId, session.AbsoluteExpiresAt, ct);
        var step = snap.Steps.FirstOrDefault(s => s.Status == StepStatus.Submitted && s.TxIds.Length > 0);
        if (step is null)
            return snap.Preview.Status == ActionPreviewStatus.Completed
                ? new ConfirmResult(ConfirmOutcome.Landed, "The transaction is confirmed on chain.", snap.Steps.SelectMany(s => s.TxIds).ToArray())
                : throw new PreviewException(PreviewError.InvalidState, "The preview has no submitted step to reconcile.");
        return await TrackAsync(session, snap.Preview, step, step.TxIds, false, ct);
    }

    /// <summary>
    /// Consent orchestrator hook (T-049): the user approved on the phone, so the same previewed group goes back to
    /// Biatec OIDC with the elevated token (held in memory only, never vaulted or sent to the browser), then follows
    /// the normal submit path. Biatec OIDC checks the receipt and the group again.
    /// </summary>
    public async Task<ConfirmResult> ResumeAfterConsentAsync(Tenant tenant, ChatSession session, Guid previewId, string elevatedBearer, CancellationToken ct)
    {
        var snap = await previews.GetAsync(session.ConversationId, previewId, session.AbsoluteExpiresAt, ct);
        var preview = snap.Preview;
        var step = snap.Steps.FirstOrDefault(s => s.Status == StepStatus.AwaitingConsent)
            ?? throw new PreviewException(PreviewError.InvalidState, "The preview has no step awaiting consent.");
        IReadOnlyList<Algorand.Algod.Model.Transactions.Transaction> group;
        string address;
        try { (group, address) = PrepareGroup(step); }
        catch (SigningVerificationException ex)
        {
            await FailAsync(preview, step, ct);
            await RecordAsync(session, "signing_refused", new JsonObject { ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(), ["reason"] = "group_mismatch" }, ct);
            return new ConfirmResult(ConfirmOutcome.Failed, ex.Message + " Nothing was signed; the preview must be rebuilt.", [], ErrorCode: "group_mismatch");
        }

        var remaining = (int)Math.Max(1, (step.ExpiresAt - clock.GetUtcNow()).TotalSeconds);
        var origin = new Uri(tenant.RedirectUri).GetLeftPart(UriPartial.Authority);
        var outcome = await signer.SignAsync(elevatedBearer, new WalletSignRequest(step.Network!, address, step.UnsignedGroupList(), Convert.FromHexString(step.SubjectHash),
            RequireConsent: false, MaxConsentSeconds: remaining, Origin: origin), ct);
        await RecordExtraAsync(session, EvidenceKinds.SigningOutcome, SigningOutcomePayload(previewId, step, outcome), CancellationToken.None);

        switch (outcome)
        {
            case WalletSignOutcome.Signed signed:
                return await SubmitAsync(tenant, session, preview, step, group, signed, ct);
            case WalletSignOutcome.Unavailable u:
                // The consent token is not kept: the user confirms again, which asks for fresh consent.
                step.Status = StepStatus.Pending;
                preview.Status = ActionPreviewStatus.Confirmed;
                return await OutageAsync(session, preview, step, u.Reason, ct);
            default:
                // A second challenge, or a refusal after approval, ends this preview; it is rebuilt.
                var error = outcome is WalletSignOutcome.Rejected r ? r.Error : "consent_not_accepted";
                await FailAsync(preview, step, ct);
                await RecordAsync(session, "signing_refused", new JsonObject
                {
                    ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(), ["error"] = error,
                }, ct);
                return new ConfirmResult(ConfirmOutcome.Failed, "Biatec OIDC did not accept the approval. Nothing was signed; the preview must be rebuilt.", [], ErrorCode: error);
        }
    }

    /// <summary>
    /// Consent orchestrator hook (T-049): the consent ended without approval. A denial cancels the action; an expired
    /// or lapsed consent expires it so the widget asks the assistant to rebuild the preview. Nothing was signed.
    /// </summary>
    public async Task<ConfirmResult> EndConsentAsync(ChatSession session, Guid previewId, bool denied, string? reason, CancellationToken ct)
    {
        var snap = await previews.GetAsync(session.ConversationId, previewId, session.AbsoluteExpiresAt, ct);
        var step = snap.Steps.FirstOrDefault(s => s.Status == StepStatus.AwaitingConsent)
            ?? throw new PreviewException(PreviewError.InvalidState, "The preview has no step awaiting consent.");
        step.Status = denied ? StepStatus.Cancelled : StepStatus.Expired;
        snap.Preview.Status = denied ? ActionPreviewStatus.Cancelled : ActionPreviewStatus.Expired;
        await db.SaveChangesAsync(ct);
        await RecordAsync(session, denied ? "consent_denied" : "consent_expired", new JsonObject
        {
            ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash, ["reason"] = reason,
        }, ct);
        return denied
            ? new ConfirmResult(ConfirmOutcome.Failed, "You declined the action on your phone. Nothing was signed.", [], ErrorCode: "consent_denied")
            : new ConfirmResult(ConfirmOutcome.Failed, "The approval request expired. Nothing was signed; the preview must be rebuilt.", [], ErrorCode: "consent_expired");
    }

    private async Task<ConfirmResult> TrackAsync(ChatSession session, ActionPreview preview, ActionStep step, IReadOnlyList<string> txIds, bool unknownSubmit, CancellationToken ct)
    {
        // A group lands atomically, so following its first transaction is enough.
        var tracked = await confirmer.WaitAsync(step.Network!, txIds[0], LastValidOf(step), ct);
        switch (tracked.State)
        {
            case TxConfirmationState.Expired:
                // Not on chain and past LastValid: it can never land, so the preview ends here and can be rebuilt.
                await FailAsync(preview, step, ct);
                await RecordAsync(session, "submission_failed", new JsonObject { ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash, ["reason"] = "expired" }, ct);
                return new ConfirmResult(ConfirmOutcome.Failed, "The transaction did not land before it expired. Nothing moved; the preview must be rebuilt.", txIds, ErrorCode: "expired");
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
                return new ConfirmResult(ConfirmOutcome.Submitted,
                    unknownSubmit ? "The submission could not be confirmed either way; the transaction may still land. Check again shortly."
                                  : "The transaction was submitted and is waiting to be confirmed.", txIds);
        }
    }

    /// <summary>The last round the step's group is valid for: the stored value, else read from the first transaction; 0 if unknown.</summary>
    private static ulong LastValidOf(ActionStep step)
    {
        if (step.LastValid is > 0) return (ulong)step.LastValid.Value;
        try { return step.UnsignedGroupList().Select(t => Algorand.Algod.Model.Transactions.Transaction.FromBase64String(t).LastValid).FirstOrDefault(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return 0; }
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

    /// <summary>Signing result as Biatec OIDC reported it, including its spending-limit decisions. Signed bytes are not recorded.</summary>
    private static JsonObject SigningOutcomePayload(Guid previewId, ActionStep step, WalletSignOutcome outcome)
    {
        var o = new JsonObject { ["previewId"] = previewId.ToString(), ["stepId"] = step.Id.ToString(), ["groupHash"] = step.SubjectHash };
        switch (outcome)
        {
            case WalletSignOutcome.Signed signed:
                o["result"] = "signed"; o["valuedEur"] = signed.ValuedEur; o["signedCount"] = signed.SignedTransactions.Count;
                break;
            case WalletSignOutcome.StepUp s:
                o["result"] = "consent_required"; o["expiresInSeconds"] = s.ExpiresInSeconds;
                break;
            case WalletSignOutcome.Rejected r:
                o["result"] = "rejected"; o["status"] = r.Status; o["error"] = r.Error;
                break;
            default:
                o["result"] = "unavailable";
                break;
        }
        return o;
    }

    /// <summary>Hooks added by T-035; separate from <see cref="RecordAsync"/>, which writes the original confirm kinds.</summary>
    private Task RecordExtraAsync(ChatSession session, string kind, JsonObject payload, CancellationToken ct) =>
        recorder is null ? Task.CompletedTask : recorder.RecordAsync(session.ConversationId, kind, payload, ct);

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
