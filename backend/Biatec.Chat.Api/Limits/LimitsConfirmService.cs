using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Limits;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Limits;

/// <summary>
/// The user's Confirm on a limits preview. Checks the <c>manage-limits</c> scope, the hash binding and the 600 second
/// gate, then re-reads the limits and refuses (server-side) if the stored body would now raise any of them, and only
/// then sends <c>PUT /wallet/limits</c> to Biatec OIDC. A consent challenge is never started from here: a lowering never
/// needs one, so one appearing is treated as a refusal.
/// </summary>
public sealed class LimitsConfirmService(
    ChatDbContext db,
    ActionPreviewService previews,
    IScopeGuard scopes,
    ITokenVault vault,
    IWalletLimitsClient limits,
    IEvidenceLog evidence,
    ILogger<LimitsConfirmService> log)
{
    public async Task<ConfirmResult> ConfirmAsync(Tenant tenant, ChatSession session, Guid previewId, string groupHash, CancellationToken ct)
    {
        var sessionId = session.Id.ToString("N");
        var peek = await previews.GetAsync(session.ConversationId, previewId, session.AbsoluteExpiresAt, ct);
        var pending = peek.Steps.FirstOrDefault(s => s.Status == StepStatus.Pending);
        if (pending is null || pending.Kind != ActionStepKind.Limits || peek.Preview.Status != ActionPreviewStatus.Proposed)
            throw new PreviewException(PreviewError.InvalidState, "The preview has no limits step awaiting confirmation.");

        // The tenant flag is enforced again here: an operator may have turned it off since the preview was made.
        if (!tenant.LimitsUpdateEnabled)
            return new ConfirmResult(ConfirmOutcome.Failed, "Changing limits from chat is not enabled here. Nothing was changed.", [], ErrorCode: "feature_disabled");

        var scope = await scopes.CheckAsync(sessionId, ElevationScopes.ManageLimits, ct);
        if (!scope.Allowed)
        {
            return scope.Reauthorize is null
                ? new ConfirmResult(ConfirmOutcome.SignInRequired, scope.Message ?? "Sign in to continue.", [], ErrorCode: "sign_in_required")
                : new ConfirmResult(ConfirmOutcome.ScopeRequired, scope.Message ?? "Changing limits needs your permission.", [], ReauthorizeScope: scope.Reauthorize, ErrorCode: "scope_required");
        }

        // The stored body must still hash to the subject the user was shown.
        Dictionary<string, string>? body = ReadBody(pending.LimitsRequest);
        if (body is null || !string.Equals(LimitsPolicy.Canonicalize(body).Hash, pending.SubjectHash, StringComparison.Ordinal))
        {
            await FailAsync(peek.Preview, pending, ct);
            await RecordAsync(session, "limits_refused", new JsonObject { ["previewId"] = previewId.ToString(), ["reason"] = "request_mismatch" }, ct);
            return new ConfirmResult(ConfirmOutcome.Failed, "The stored limits change does not match its preview. Nothing was changed; ask again.", [], ErrorCode: "request_mismatch");
        }

        // The user's Confirm: hash, expiry and the 600 second gate (throws PreviewException).
        var snapshot = await previews.ConfirmAsync(session.ConversationId, previewId, groupHash, session.AbsoluteExpiresAt, ct);
        var preview = snapshot.Preview;
        var step = snapshot.Steps.First(s => s.Id == pending.Id);

        try
        {
            return await ApplyAsync(session, sessionId, preview, step, body, ct);
        }
        catch (Exception)
        {
            // Nothing was sent unless the step moved on: put the preview back to awaiting confirmation.
            if (step.Status == StepStatus.Pending) await RevertAsync(preview, CancellationToken.None);
            throw;
        }
    }

    private async Task<ConfirmResult> ApplyAsync(ChatSession session, string sessionId, Biatec.Chat.Core.Data.ActionPreview preview, Biatec.Chat.Core.Data.ActionStep step,
        Dictionary<string, string> body, CancellationToken ct)
    {
        string? bearer;
        try { bearer = await vault.GetAccessTokenAsync(sessionId, ct); }
        catch (VaultRefreshException)
        {
            await RevertAsync(preview, ct);
            return new ConfirmResult(ConfirmOutcome.SignInUnavailable, ConfirmService.OutageMessage, [], ErrorCode: "sign_in_unavailable");
        }
        if (bearer is null)
        {
            await RevertAsync(preview, ct);
            return new ConfirmResult(ConfirmOutcome.SignInRequired, "Sign in to continue.", [], ErrorCode: "sign_in_required");
        }

        // Server-side lowering-only check against the limits as they are right now.
        var current = await limits.GetAsync(bearer, ct);
        if (!current.Ok) return await UpstreamAsync(preview, current.Failure, current.Error, ct);
        if (LimitsPolicy.CheckLowering(current.Limits!, body) is { } refusal)
        {
            await FailAsync(preview, step, ct);
            await RecordAsync(session, "limits_refused", new JsonObject { ["previewId"] = preview.Id.ToString(), ["reason"] = refusal.Code }, ct);
            return new ConfirmResult(ConfirmOutcome.Failed, refusal.Message + " Nothing was changed.", [], ErrorCode: refusal.Code);
        }

        var put = await limits.PutAsync(bearer, body, ct);
        if (!put.Applied)
        {
            if (put.Failure == LimitsFailure.StepUp)
            {
                // A lowering must not need consent. Never start or poll one; refuse.
                await FailAsync(preview, step, ct);
                await RecordAsync(session, "limits_refused", new JsonObject { ["previewId"] = preview.Id.ToString(), ["reason"] = "unexpected_step_up" }, ct);
                return new ConfirmResult(ConfirmOutcome.Failed, "Biatec asked for approval, which a lowering never needs, so nothing was changed. Chat cannot raise limits.", [], ErrorCode: "unexpected_step_up");
            }
            return await UpstreamAsync(preview, put.Failure, put.Error, ct);
        }

        step.Status = StepStatus.Confirmed;
        preview.Status = ActionPreviewStatus.Completed;
        await db.SaveChangesAsync(CancellationToken.None);
        await RecordAsync(session, "limits_changed", new JsonObject
        {
            ["previewId"] = preview.Id.ToString(), ["stepId"] = step.Id.ToString(), ["requestHash"] = step.SubjectHash,
        }, CancellationToken.None);
        return new ConfirmResult(ConfirmOutcome.Landed, "Your spending limits were lowered.", []);
    }

    /// <summary>A failed upstream call. An outage or an expired sign-in leaves the preview confirmable; a refusal ends it.</summary>
    private async Task<ConfirmResult> UpstreamAsync(Biatec.Chat.Core.Data.ActionPreview preview, LimitsFailure? failure, string? error, CancellationToken ct)
    {
        switch (failure)
        {
            case LimitsFailure.Unavailable:
                await RevertAsync(preview, ct);
                return new ConfirmResult(ConfirmOutcome.SignInUnavailable, ConfirmService.OutageMessage, [], ErrorCode: "sign_in_unavailable");
            case LimitsFailure.Unauthorized:
                await RevertAsync(preview, ct);
                return new ConfirmResult(ConfirmOutcome.SignInRequired, "Your sign-in is no longer valid. Please sign in again.", [], ErrorCode: "sign_in_required");
            default:
                var step = await db.ActionSteps.FirstAsync(s => s.PreviewId == preview.Id && s.Status == StepStatus.Pending, ct);
                await FailAsync(preview, step, ct);
                return new ConfirmResult(ConfirmOutcome.Failed, "The limits change was refused. Nothing was changed; ask again to get a new preview.", [],
                    ErrorCode: failure == LimitsFailure.Forbidden ? "forbidden" : "rejected");
        }
    }

    private static Dictionary<string, string>? ReadBody(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) return null;
                d[p.Name] = p.Value.GetString()!;
            }
            return d;
        }
        catch (JsonException) { return null; }
    }

    private async Task RevertAsync(Biatec.Chat.Core.Data.ActionPreview preview, CancellationToken ct)
    {
        if (preview.Status != ActionPreviewStatus.Confirmed) return;
        preview.Status = ActionPreviewStatus.Proposed;
        await db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(Biatec.Chat.Core.Data.ActionPreview preview, Biatec.Chat.Core.Data.ActionStep step, CancellationToken ct)
    {
        step.Status = StepStatus.Failed;
        preview.Status = ActionPreviewStatus.Failed;
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordAsync(ChatSession session, string kind, JsonObject payload, CancellationToken ct)
    {
        try { await evidence.AppendAsync(session.ConversationId, kind, payload, ct: ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Could not record evidence {Kind}: {ErrorType}", kind, ex.GetType().Name);
        }
    }
}
