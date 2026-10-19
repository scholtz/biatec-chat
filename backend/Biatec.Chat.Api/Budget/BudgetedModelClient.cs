using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Tenancy;
using SessionTier = Biatec.Chat.Core.Data.SessionTier;
using Tenant = Biatec.Chat.Core.Data.Tenant;
using Message = Amazon.BedrockRuntime.Model.Message;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Budget;

/// <summary>The tenant a model call is made for; null outside a tenant request.</summary>
public interface IBudgetTenantSource
{
    Tenant? Current { get; }
}

public sealed class HttpBudgetTenantSource(IHttpContextAccessor http) : IBudgetTenantSource
{
    public Tenant? Current => http.HttpContext?.GetTenant();
}

/// <summary>Both the tenant's and the global budget are used up, for every tier.</summary>
public sealed class BudgetExhaustedException() : Exception("The Bedrock budget for this window is used up.");

/// <summary>
/// Caps Bedrock spend per tenant and globally around any <see cref="IModelClient"/>. Each call's usage is priced
/// and added to the window's counters. Before a call, anonymous sessions are held to <c>(1 - reserved share)</c> of
/// each budget: past it the breaker answers from the cached-answer store, or with a refusal, and Bedrock is not
/// called. Authenticated sessions may use the whole budget and get <see cref="BudgetExhaustedException"/> past it.
/// The check reads the counters before the call, so calls already in flight can overshoot a budget by their own
/// cost; it is a cost limiter, not an exact quota. Redis errors fail open (logged), because the budget must never
/// take the chat down on its own.
/// </summary>
public sealed class BudgetedModelClient(
    IModelClient inner,
    IBudgetLedger ledger,
    IAnswerCache cache,
    IBudgetTenantSource tenants,
    IOptions<BudgetOptions> options,
    TimeProvider clock,
    ILogger<BudgetedModelClient> log,
    Biatec.Chat.Api.Telemetry.ChatMetrics? metrics = null) : IModelClient
{
    /// <summary>The refusal reason in <c>biatec.chat.refusals</c>; a cached answer is not counted.</summary>
    public const string RefusalReason = "budget_exhausted";

    public const string AnonymousRefusal = "Biatec Chat has reached its usage limit for anonymous questions for now. Please try again later.";
    public const string SignInHint = " Signing in keeps the assistant available.";
    private const string NoTenant = "-";

    private readonly BudgetOptions _o = options.Value;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_o.Enabled)
        {
            await foreach (var e in inner.StreamAsync(request, ct))
                yield return e;
            yield break;
        }

        var tenant = tenants.Current;
        var tenantId = tenant?.Id ?? NoTenant;
        var tenantCap = ToMicro(TenantUsd(tenant));
        var globalCap = ToMicro(_o.GlobalUsdPerWindow);
        var anonymous = request.Profile == ModelProfile.Anonymous;
        var question = anonymous ? SingleQuestion(request.Messages) : null;

        var spend = await TryGetSpendAsync(tenantId, ct);
        if (spend is { } s)
        {
            var fullyUsed = s.TenantMicroUsd >= tenantCap || s.GlobalMicroUsd >= globalCap;
            var share = 1m - _o.ReservedAuthenticatedShare;
            var anonymousUsed = s.TenantMicroUsd >= (long)(tenantCap * share) || s.GlobalMicroUsd >= (long)(globalCap * share);
            if (anonymous && anonymousUsed)
            {
                log.LogWarning("Bedrock budget breaker open for anonymous sessions of tenant {Tenant}", tenantId);
                var cached = question is null ? null : await TryGetCachedAsync(tenantId, question, ct);
                if (cached is null) metrics?.Refusal(SessionTier.Anonymous, RefusalReason);
                foreach (var e in Answer(cached ?? AnonymousRefusal + (fullyUsed ? "" : SignInHint)))
                    yield return e;
                yield break;
            }
            if (fullyUsed)
            {
                log.LogWarning("Bedrock budget used up for tenant {Tenant}", tenantId);
                metrics?.Refusal(SessionTier.Authenticated, RefusalReason);
                throw new BudgetExhaustedException();
            }
        }

        var text = new StringBuilder();
        var toolUse = false;
        string? stopReason = null;
        await foreach (var e in inner.StreamAsync(request, ct))
        {
            switch (e)
            {
                case ModelTextDelta d: text.Append(d.Text); break;
                case ModelToolUse: toolUse = true; break;
                case ModelMessageStop stop: stopReason = stop.StopReason; break;
                case ModelUsage u: await TryAddAsync(tenantId, ModelPrices.MicroUsd(u, ModelPrices.For(u.ModelId, _o.Prices)), ct); break;
            }
            yield return e;
        }

        // Only a complete, tool-free answer to a conversation's single question is reusable as a cached answer.
        if (question is not null && !toolUse && stopReason == "end_turn" && text.Length > 0)
            await TryPutCachedAsync(tenantId, question, text.ToString(), ct);
    }

    /// <summary>The tenant's <c>bedrockBudgets.usdPerWindow</c>, or the configured default.</summary>
    public decimal TenantUsd(Tenant? tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant?.BedrockBudgets)) return _o.TenantUsdPerWindow;
        try
        {
            using var doc = JsonDocument.Parse(tenant.BedrockBudgets);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("usdPerWindow", out var v) && v.TryGetDecimal(out var usd) && usd >= 0
                ? usd : _o.TenantUsdPerWindow;
        }
        catch (JsonException)
        {
            return _o.TenantUsdPerWindow;
        }
    }

    /// <summary>
    /// The visitor's question when the conversation holds exactly one user text turn, else null (a later turn
    /// depends on the conversation, so its answer is not reusable). Host page context blocks are not part of it.
    /// </summary>
    public static string? SingleQuestion(IReadOnlyList<Message> messages)
    {
        var userTurns = messages.Where(m => m.Role == ConversationRole.User && m.Content is { Count: > 0 } c && c[0].Text is not null).ToList();
        if (userTurns.Count != 1) return null;
        var q = userTurns[0].Content[0].Text;
        return string.IsNullOrWhiteSpace(q) ? null : q;
    }

    private static IEnumerable<ModelStreamEvent> Answer(string text)
    {
        yield return new ModelTextDelta(text);
        yield return new ModelMessageStop("end_turn", new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = text }] });
    }

    private static long ToMicro(decimal usd) => (long)Math.Min(usd * 1_000_000m, long.MaxValue);

    private async Task<BudgetSpend?> TryGetSpendAsync(string tenantId, CancellationToken ct)
    {
        try { return await ledger.GetAsync(tenantId, clock.GetUtcNow(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Bedrock budget read failed, not enforcing: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    private async Task TryAddAsync(string tenantId, long microUsd, CancellationToken ct)
    {
        // The model was already paid for, so the spend is recorded even if the caller has gone.
        try { await ledger.AddAsync(tenantId, microUsd, clock.GetUtcNow(), CancellationToken.None); }
        catch (Exception ex)
        {
            log.LogWarning("Bedrock budget update failed, {MicroUsd} micro-USD not counted: {ErrorType}", microUsd, ex.GetType().Name);
        }
    }

    private async Task<string?> TryGetCachedAsync(string tenantId, string question, CancellationToken ct)
    {
        try { return await cache.GetAsync(tenantId, question, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Cached answer read failed: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    private async Task TryPutCachedAsync(string tenantId, string question, string answer, CancellationToken ct)
    {
        try { await cache.PutAsync(tenantId, question, answer, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Cached answer write failed: {ErrorType}", ex.GetType().Name);
        }
    }
}
