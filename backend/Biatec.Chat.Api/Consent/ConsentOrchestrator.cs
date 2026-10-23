using System.Text.Json.Nodes;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Evidence.Recording;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Consent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ConsentRequestStatus = Biatec.Chat.Core.Data.ConsentStatus;

namespace Biatec.Chat.Api.Consent;

/// <summary>
/// One step of a consent: the status the widget shows and, once the consent has ended, the outcome of the confirm
/// path it completed (signed and submitted, denied, or expired).
/// </summary>
public sealed record ConsentAdvance(Contracts.ConsentStatus Status, ConfirmResult? Result, TimeSpan? PollAfter = null)
{
    public bool Terminal => Status.State != ConsentState.Pending;
}

/// <summary>
/// Completes the confirm path when Biatec OIDC asked for phone approval (T-049). The 401 step-up is handled by
/// <see cref="ConfirmService"/>, which stores the <see cref="ConsentRequest"/> with the binding code. Each call here
/// polls the CIBA token endpoint once: on approval the receipt is stored verbatim and recorded in evidence, then the
/// same previewed group is signed again with the elevated token and submitted. A denial cancels the action; an expired
/// consent expires the preview so it is rebuilt. Nothing here reaches the browser except the binding code and state.
/// </summary>
public sealed class ConsentOrchestrator(
    ChatDbContext db,
    ConfirmService confirm,
    ICibaTokenClient ciba,
    IOidcMetadataProvider metadata,
    ISecretResolver secrets,
    IOptions<OidcOptions> oidc,
    IEvidenceLog evidence,
    TimeProvider clock,
    ILogger<ConsentOrchestrator> log,
    IEvidenceRecorder? recorder = null)
{
    /// <summary>Upper bound of the poll interval after repeated <c>slow_down</c> answers.</summary>
    public const int MaxPollIntervalSeconds = 60;

    public async Task<ConsentAdvance> AdvanceAsync(Tenant tenant, ChatSession session, Guid previewId, CancellationToken ct)
    {
        var steps = await (from s in db.ActionSteps
                           join p in db.ActionPreviews on s.PreviewId equals p.Id
                           where p.Id == previewId && p.ConversationId == session.ConversationId
                           select s).ToListAsync(ct);
        if (steps.Count == 0) throw new PreviewException(PreviewError.NotFound, "Unknown preview.");
        var stepIds = steps.Select(s => s.Id).ToList();
        var request = (await db.ConsentRequests.Where(c => stepIds.Contains(c.StepId)).ToListAsync(ct))
            .OrderByDescending(c => c.ExpiresAt).FirstOrDefault();
        if (request is null) return new ConsentAdvance(StatusOf(previewId, steps[0].Id, ConsentState.NotRequired, null, null), null);

        var step = steps.First(s => s.Id == request.StepId);
        if (request.Status != ConsentRequestStatus.Pending || step.Status != StepStatus.AwaitingConsent)
            return new ConsentAdvance(StatusOf(previewId, step.Id, StateOf(request.Status), null, request.ExpiresAt), null);

        if (clock.GetUtcNow() >= request.ExpiresAt)
            return await EndAsync(session, previewId, request, ConsentRequestStatus.Expired, denied: false, "window_elapsed", ct);

        // The interval is enforced here, not left to the caller: a reconnected stream, a second tab or the plain poll endpoint cannot poll faster
        // than Biatec OIDC asked. One caller wins the slot (atomic), the others get the same pending status and when to come back.
        var now = clock.GetUtcNow();
        var interval = TimeSpan.FromSeconds(Math.Max(1, request.PollIntervalSeconds));
        if (request.LastPolledAt is { } last && now - last < interval)
            return new ConsentAdvance(StatusOf(previewId, step.Id, ConsentState.Pending, request.BindingCode, request.ExpiresAt), null, interval - (now - last));
        var since = now - interval;
        var slot = await db.ConsentRequests.Where(c => c.Id == request.Id && c.Status == ConsentRequestStatus.Pending && (c.LastPolledAt == null || c.LastPolledAt <= since))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastPolledAt, now), CancellationToken.None);
        if (slot == 0)
            return new ConsentAdvance(StatusOf(previewId, step.Id, ConsentState.Pending, request.BindingCode, request.ExpiresAt), null, interval);

        CibaPollOutcome polled;
        try
        {
            var meta = await metadata.GetAsync(ct);
            polled = await ciba.PollAsync(meta.TokenEndpoint, tenant.OidcClientId, secrets.Resolve(tenant.ClientSecretRef),
                oidc.Value.McpResource, request.AuthReqId, ct);
        }
        catch (OidcException ex)
        {
            log.LogWarning("Consent poll could not reach Biatec OIDC: {ErrorType}", ex.GetType().Name);
            polled = new CibaPollOutcome.Unavailable("oidc_exception");
        }

        switch (polled)
        {
            case CibaPollOutcome.Denied d:
                return await EndAsync(session, previewId, request, ConsentRequestStatus.Denied, denied: true, d.Reason ?? "deny", ct);
            case CibaPollOutcome.Expired:
                return await EndAsync(session, previewId, request, ConsentRequestStatus.Expired, denied: false, "expired_token", ct);
            case CibaPollOutcome.Approved a:
                return await ApprovedAsync(tenant, session, previewId, request, step, a, ct);
            case CibaPollOutcome.Pending { SlowDown: true }:
            {
                // RFC 8628 3.5 / CIBA 10.2: the server asked for a slower rate, so every later poll waits 5 seconds longer (kept, so a new stream starts slow too).
                await db.ConsentRequests.Where(c => c.Id == request.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.PollIntervalSeconds, c => Math.Min(c.PollIntervalSeconds + 5, MaxPollIntervalSeconds)), CancellationToken.None); // atomic: two slow_downs are +10
                var next = await db.ConsentRequests.AsNoTracking().Where(c => c.Id == request.Id).Select(c => c.PollIntervalSeconds).SingleAsync(CancellationToken.None);
                return new ConsentAdvance(StatusOf(previewId, step.Id, ConsentState.Pending, request.BindingCode, request.ExpiresAt), null, TimeSpan.FromSeconds(next));
            }
            default:
                // Pending, an already redeemed request (another poll took it) or an outage: still waiting, at the interval Biatec OIDC gave.
                return new ConsentAdvance(StatusOf(previewId, step.Id, ConsentState.Pending, request.BindingCode, request.ExpiresAt), null, TimeSpan.FromSeconds(request.PollIntervalSeconds));
        }
    }

    private async Task<ConsentAdvance> ApprovedAsync(Tenant tenant, ChatSession session, Guid previewId, ConsentRequest request, Biatec.Chat.Core.Data.ActionStep step,
        CibaPollOutcome.Approved approved, CancellationToken ct)
    {
        // Biatec OIDC always returns the signed receipt with the token. Without it there is nothing to keep as proof.
        if (string.IsNullOrEmpty(approved.Receipt))
            return await EndAsync(session, previewId, request, ConsentRequestStatus.Cancelled, denied: false, "receipt_missing", ct, "receipt_missing");

        // Exactly one poller wins the approval (the token can be redeemed once anyway).
        var claimed = await db.ConsentRequests.Where(c => c.Id == request.Id && c.Status == ConsentRequestStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ConsentRequestStatus.Approved).SetProperty(c => c.Receipt, approved.Receipt), CancellationToken.None);
        if (claimed == 0)
            return new ConsentAdvance(StatusOf(previewId, step.Id, ConsentState.Pending, request.BindingCode, request.ExpiresAt), null);
        request.Status = ConsentRequestStatus.Approved;
        request.Receipt = approved.Receipt;
        // The open-consent exemption ends here: restart the idle window so signing is not swept mid-flow (T-080).
        await Sessions.SessionLiveness.TouchByIdAsync(db, session.Id, clock.GetUtcNow(), CancellationToken.None);

        var payload = EvidencePayloads.ConsentReceipt(previewId, step.Id, request.AuthReqId, step.SubjectHash, approved.Receipt);
        try
        {
            if (recorder is not null) await recorder.RecordAsync(session.ConversationId, EvidenceKinds.ConsentReceipt, payload, CancellationToken.None);
            else await evidence.AppendAsync(session.ConversationId, EvidenceKinds.ConsentReceipt, payload, ct: CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Could not record the consent receipt: {ErrorType}", ex.GetType().Name);
        }

        ConfirmResult result;
        try { result = await confirm.ResumeAfterConsentAsync(tenant, session, previewId, approved.AccessToken, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The approval is used up (one-shot token), so the step must not stay AwaitingConsent: end it so the preview is rebuilt.
            log.LogError("Resuming after consent failed: {ErrorType}", ex.GetType().Name);
            try { result = await confirm.EndConsentAsync(session, previewId, denied: false, "resume_failed", CancellationToken.None, "resume_failed"); }
            catch (Exception) { result = new ConfirmResult(ConfirmOutcome.Failed, "The approved action could not be completed. Nothing was submitted; ask for the preview again.", [], ErrorCode: "resume_failed"); }
        }
        return new ConsentAdvance(StatusOf(previewId, step.Id, ConsentState.Approved, request.BindingCode, request.ExpiresAt), result);
    }

    private async Task<ConsentAdvance> EndAsync(ChatSession session, Guid previewId, ConsentRequest request, ConsentRequestStatus status,
        bool denied, string reason, CancellationToken ct, string? errorCode = null)
    {
        await db.ConsentRequests.Where(c => c.Id == request.Id && c.Status == ConsentRequestStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, status), CancellationToken.None);
        request.Status = status;
        db.Entry(request).State = EntityState.Unchanged;
        await Sessions.SessionLiveness.TouchByIdAsync(db, session.Id, clock.GetUtcNow(), CancellationToken.None);
        var result = await confirm.EndConsentAsync(session, previewId, denied, reason, ct, errorCode);
        return new ConsentAdvance(StatusOf(previewId, request.StepId, denied ? ConsentState.Denied : ConsentState.TimedOut, null, request.ExpiresAt), result);
    }

    private static ConsentState StateOf(ConsentRequestStatus s) => s switch
    {
        ConsentRequestStatus.Pending => ConsentState.Pending,
        ConsentRequestStatus.Approved => ConsentState.Approved,
        ConsentRequestStatus.Denied or ConsentRequestStatus.Cancelled => ConsentState.Denied,
        _ => ConsentState.TimedOut,
    };

    private static Contracts.ConsentStatus StatusOf(Guid previewId, Guid stepId, ConsentState state, string? code, DateTimeOffset? expires) => new()
    {
        PreviewId = previewId.ToString(), StepId = stepId.ToString(), State = state,
        BindingCode = state == ConsentState.Pending ? code : null, ExpiresAt = expires,
    };
}
