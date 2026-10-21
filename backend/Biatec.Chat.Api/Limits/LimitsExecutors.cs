using System.Text.Json.Nodes;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Limits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Limits;

/// <summary>Shared guards and plumbing for the two limits executors. The ToolGate has already checked tier, flag, scope visibility and schema; the essentials are re-checked here.</summary>
public abstract class LimitsExecutorBase(ITokenVault vault, IWalletLimitsClient limits) : IToolExecutor
{
    public abstract string CapabilityName { get; }
    public abstract string? StatusLabel(JsonNode arguments);
    protected abstract Task<ToolExecutionResult> RunAsync(ToolExecutionContext context, JsonNode arguments, string bearer, CancellationToken ct);

    protected IWalletLimitsClient Limits => limits;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        if (context.Tier != Biatec.Chat.Core.Data.SessionTier.Authenticated)
            return new ToolExecutionResult("This needs a signed-in user. Call requestSignIn instead.", true);
        string? bearer;
        try { bearer = await vault.GetAccessTokenAsync(context.SessionId, ct); }
        catch (VaultRefreshException)
        {
            return new ToolExecutionResult("The sign-in service is temporarily unavailable. Tell the user to try again shortly.", true);
        }
        if (bearer is null) return new ToolExecutionResult("The sign-in has expired. Ask the user to sign in again.", true);
        return await RunAsync(context, arguments, bearer, ct);
    }

    /// <summary>Maps a failed OIDC call to a message for the model. Never includes upstream text.</summary>
    protected static ToolExecutionResult Failure(LimitsFailure? f) => f switch
    {
        LimitsFailure.Unavailable => new ToolExecutionResult("The limits service is temporarily unavailable. Tell the user to try again later.", true),
        LimitsFailure.Unauthorized => new ToolExecutionResult("The sign-in has expired. Ask the user to sign in again.", true),
        LimitsFailure.Forbidden => new ToolExecutionResult("This account is not allowed to read or change limits from chat.", true),
        _ => new ToolExecutionResult("The limits could not be read. Tell the user to try again.", true),
    };
}

/// <summary><c>limits.view</c>: the user's spending limits via Biatec OIDC <c>GET /wallet/limits</c>, each with its currency.</summary>
public sealed class LimitsViewExecutor(ITokenVault vault, IWalletLimitsClient limits) : LimitsExecutorBase(vault, limits)
{
    public override string CapabilityName => "limits.view";
    public override string? StatusLabel(JsonNode arguments) => "Reading your spending limits";

    protected override async Task<ToolExecutionResult> RunAsync(ToolExecutionContext context, JsonNode arguments, string bearer, CancellationToken ct)
    {
        var r = await Limits.GetAsync(bearer, ct);
        if (!r.Ok) return Failure(r.Failure);
        var o = new JsonObject
        {
            ["limits"] = new JsonArray(r.Limits!.Select(l => (JsonNode?)new JsonObject
            {
                ["name"] = l.Key, ["period"] = l.Period, ["amount"] = l.Amount, ["currency"] = l.Currency, ["display"] = l.Display,
            }).ToArray()),
            ["canLower"] = context.Tenant?.LimitsUpdateEnabled == true,
            ["canRaise"] = false,
        };
        return new ToolExecutionResult(o.ToJsonString());
    }
}

/// <summary>
/// <c>limits.update</c>, lowering only. Reads the current limits, refuses any raise on the server, and builds an action
/// preview bound to the hash of the canonical request body. Nothing is applied until the user confirms
/// (<see cref="LimitsConfirmService"/>). Hidden by the gate, and refused here again, on tenants without the flag.
/// </summary>
public sealed class LimitsUpdateExecutor(ITokenVault vault, IWalletLimitsClient limits, IServiceScopeFactory scopes, TimeProvider clock)
    : LimitsExecutorBase(vault, limits)
{
    /// <summary>Longer than the 600 second Confirm gate, so the user has time to read.</summary>
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);

    public override string CapabilityName => "limits.update";
    public override string? StatusLabel(JsonNode arguments) => "Preparing a limits change";

    protected override async Task<ToolExecutionResult> RunAsync(ToolExecutionContext context, JsonNode arguments, string bearer, CancellationToken ct)
    {
        if (context.Tenant?.LimitsUpdateEnabled != true)
            return new ToolExecutionResult("Changing limits from chat is not enabled for this site.", true);

        if (!context.Scopes.Contains(ElevationScopes.ManageLimits, StringComparer.Ordinal))
            return RequestScope(context);

        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var period in LimitsPolicy.Periods)
            if (arguments[period]?.GetValue<string>() is { } v) requested[period] = v;

        var current = await Limits.GetAsync(bearer, ct);
        if (!current.Ok) return Failure(current.Failure);

        var (plan, refusal) = LimitsPolicy.Plan(current.Limits!, requested);
        if (plan is null) return new ToolExecutionResult(refusal!.Message + " No change was prepared.", true);

        var expires = clock.GetUtcNow().Add(PreviewLifetime);
        var canonical = new JsonObject
        {
            ["capability"] = CapabilityName,
            ["requestHash"] = plan.RequestHash,
            ["changes"] = new JsonArray(plan.Changes.Select(c => (JsonNode?)new JsonObject
            {
                ["period"] = c.Period, ["from"] = c.From, ["to"] = c.To, ["currency"] = c.Currency,
            }).ToArray()),
        };
        await using var scope = scopes.CreateAsyncScope();
        var previews = scope.ServiceProvider.GetRequiredService<ActionPreviewService>();
        PreviewSnapshot snap;
        try
        {
            snap = await previews.CreateAsync(new NewPreviewSpec(context.ConversationId, CapabilityName, canonical.ToJsonString(),
            [
                new NewStepSpec(ActionStepKind.Limits, plan.RequestHash, expires, LimitsRequest: plan.CanonicalBody, Network: "biatec-oidc"),
            ]), ct);
        }
        catch (PreviewException ex)
        {
            return new ToolExecutionResult("The limits change could not be prepared: " + ex.Message, true);
        }

        var intent = "Lower your spending limit" + (plan.Changes.Count > 1 ? "s: " : ": ")
            + string.Join(", ", plan.Changes.Select(c => $"{c.Period} {c.From} to {c.To} {c.Currency}"));
        var card = new Contracts.ActionPreview
        {
            PreviewId = snap.Preview.Id.ToString(),
            Capability = CapabilityName,
            Intent = intent,
            Network = "biatec-oidc",
            GenesisId = "none",
            Status = Contracts.ActionPreviewStatus.AwaitingConfirmation,
            ExpectsAuthenticatorApproval = false,
            ApprovalReason = "Lowering a limit applies at once and needs no phone approval. Raising one is not possible from chat.",
            ExpiresAt = expires,
            Steps =
            [
                new Contracts.ActionStep
                {
                    StepId = snap.Steps[0].Id.ToString(), Index = 0, Title = intent, Status = Contracts.ActionPreviewStatus.AwaitingConfirmation,
                    GroupHash = plan.RequestHash,
                    SimulationSummary = string.Join("; ", plan.Changes.Select(c => $"{c.Period}: {c.From} {c.Currency} to {c.To} {c.Currency}")),
                },
            ],
        };
        return new ToolExecutionResult(
            "A limits change preview card is now shown to the user. Nothing has been changed yet; it applies only after the user confirms the card. "
            + "Summarise: " + intent + ". Chat can only lower limits, never raise them.",
            Events: [new PreviewCardEvent(card)]);
    }

    private static ToolExecutionResult RequestScope(ToolExecutionContext context)
    {
        var origin = context.Tenant is { } t ? Biatec.Chat.Api.Hosting.BackendUrls.TryBase(t) : null;
        if (!context.SignInAvailable || origin is null)
            return new ToolExecutionResult("Changing limits needs the 'manage-limits' permission, and it cannot be requested on this page. Tell the user to open the Biatec site and authorize it there. Nothing was changed.", true);
        var card = new SignInCardEvent(origin + "/auth/elevate/popup", SignInReason.ElevateScope, ElevationScopes.ManageLimits,
            "Authorize limit management so Biatec AI can lower your spending limits.", Unavailable: false);
        return new ToolExecutionResult(
            "The user has not yet allowed limit management. An authorization button is now shown. Nothing was changed; ask them to authorize and then repeat the request.",
            Events: [card]);
    }
}

public static class LimitsRegistration
{
    /// <summary>
    /// Registers the limits executors and the confirm handler. Signing-style calls go straight to Biatec OIDC (the configured
    /// issuer). Needs ITokenVault, IScopeGuard and IEvidenceLog (registered by the vault, elevation and evidence modules).
    /// </summary>
    public static IServiceCollection AddLimitsTools(this IServiceCollection services)
    {
        services.TryAddScoped<ActionPreviewService>();
        services.AddHttpClient<IWalletLimitsClient, WalletLimitsClient>((sp, c) =>
        {
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OidcOptions>>().Value.Issuer.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<IToolExecutor, LimitsViewExecutor>();
        services.AddSingleton<IToolExecutor, LimitsUpdateExecutor>();
        services.TryAddScoped<LimitsConfirmService>();
        return services;
    }
}
