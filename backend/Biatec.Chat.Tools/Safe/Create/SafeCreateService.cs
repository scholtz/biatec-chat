using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Wallet.Transfer;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Tools.Safe.Create;

public enum SafeCreateState
{
    /// <summary>Nothing was previewed; <see cref="SafeCreateOutcome.Message"/> says why.</summary>
    Error,
    /// <summary>A step (deploy or signer-group setup) waits for the user's Confirm on the card.</summary>
    AwaitingConfirmation,
    /// <summary>A step is being signed or is waiting to land.</summary>
    Busy,
    /// <summary>The safe is deployed but its account needs ALGO for box storage, sent with a separate wallet.transfer.</summary>
    NeedsFunding,
    /// <summary>Deployed and bootstrapped: the safe exists with its signer group.</summary>
    Created,
}

public sealed record SafeCreateOutcome(
    SafeCreateState State,
    string Message,
    PreviewSnapshot? Snapshot = null,
    SafeCreateRequest? Request = null,
    ulong? AppId = null,
    string? SafeAddress = null,
    ulong? FundingMicroAlgo = null,
    string? Simulation = null,
    string? ResumedDeployTxId = null)
{
    public static SafeCreateOutcome Error(string message) => new(SafeCreateState.Error, message);
}

public sealed class SafeCreateOptions
{
    public const string Section = "SafeCreate";

    /// <summary>
    /// ALGO (in microAlgo) the safe's account must hold before the signer-group setup, for its minimum balance and the
    /// group and member boxes. Sent by the user as a separate wallet.transfer.
    /// </summary>
    public ulong MinimumFundingMicroAlgo { get; set; } = 500_000;
}

/// <summary>
/// <c>safe.create</c>: a two-step preview. Step 1 deploys the safe (a single application-create call, valued at
/// zero). Once it has landed the app id is read from the chain, the safe's account is checked for the ALGO its boxes
/// need (funding is a separate <c>wallet.transfer</c>, never part of these groups), and step 2 (bootstrap and
/// finalize with the requested signer group) is built. Each step is built by algo-safe in the sidecar and signed
/// through the shared confirm path; this never signs.
/// </summary>
public sealed class SafeCreateService(
    IBiatecMcpClient mcp,
    ISafeBuilder builder,
    ISafeChainReader chain,
    ActionPreviewService previews,
    ChatDbContext db,
    TimeProvider clock,
    SafeCreateOptions? options = null)
{
    public const string Capability = "safe.create";
    public const int TotalSteps = 2;

    private readonly SafeCreateOptions _options = options ?? new SafeCreateOptions();

    public async Task<SafeCreateOutcome> StartAsync(McpCallContext ctx, Guid conversationId, string tenantId, SafeCreateRequest request, CancellationToken ct)
    {
        // Only the user's own addresses may deploy and set up the safe.
        var own = await mcp.ListAlgorandAddressesAsync(ctx, ct);
        if (own.IsError) return SafeCreateOutcome.Error("The addresses could not be read. Tell the user to try again.");
        if (!McpWalletJson.Addresses(own.Json).Contains(request.From, StringComparer.Ordinal))
            return SafeCreateOutcome.Error("That address does not belong to the signed-in user. Use wallet.listAddresses to see the user's addresses.");

        try
        {
            var existing = await FindExistingAsync(conversationId, request, ct);
            if (existing is not null) return await ContinueAsync(conversationId, tenantId, request, existing, ct);
            // A deployment of this exact safe already landed (its later setup failed or was cancelled): resume at
            // funding and setup for that app id instead of deploying a second safe.
            var deployed = await FindLandedDeployAsync(conversationId, request, ct);
            return deployed is not null
                ? await ResumeAsync(conversationId, tenantId, request, deployed, ct)
                : await StartDeployAsync(conversationId, request, null, ct);
        }
        catch (PreviewException e) { return SafeCreateOutcome.Error(e.Message); }
    }

    private async Task<List<ActionPreview>> SameRequestAsync(Guid conversationId, SafeCreateRequest request, bool activeOnly, CancellationToken ct)
    {
        var q = db.ActionPreviews.Where(p => p.ConversationId == conversationId && p.Capability == Capability);
        if (activeOnly)
            q = q.Where(p => p.RebuiltAsPreviewId == null && p.Status != ActionPreviewStatus.Failed && p.Status != ActionPreviewStatus.Cancelled);
        var candidates = await q.ToListAsync(ct);
        return candidates.Where(p => SafeCreateRequest.FromCanonical(p.CanonicalJson) is { } r && r.SameAs(request))
            .OrderByDescending(p => p.ExpiresAt).ToList();
    }

    private async Task<ActionPreview?> FindExistingAsync(Guid conversationId, SafeCreateRequest request, CancellationToken ct) =>
        (await SameRequestAsync(conversationId, request, activeOnly: true, ct)).FirstOrDefault();

    /// <summary>The transaction id of a landed deployment of this request, in any preview of the conversation whatever its later status.</summary>
    private async Task<string?> FindLandedDeployAsync(Guid conversationId, SafeCreateRequest request, CancellationToken ct)
    {
        foreach (var p in await SameRequestAsync(conversationId, request, activeOnly: false, ct))
        {
            var steps = await db.ActionSteps.AsNoTracking().Where(s => s.PreviewId == p.Id).OrderBy(s => s.Seq).ToListAsync(ct);
            if (Read(p, steps).DeployTxId is { } tx) return tx;
        }
        return null;
    }

    /// <summary>Where a safe.create preview stands. A resume preview holds only the setup step; its deployment landed in an earlier preview.</summary>
    private sealed record Progress(string? DeployTxId, ActionStep? Bootstrap, bool Resume);

    private static Progress Read(ActionPreview preview, IReadOnlyList<ActionStep> steps)
    {
        if (ResumedDeployTxId(preview.CanonicalJson) is { } resumed) return new Progress(resumed, steps.FirstOrDefault(), true);
        var deploy = steps.FirstOrDefault(s => s.Seq == 0);
        var tx = deploy is { Status: StepStatus.Confirmed } && deploy.TxIds.Length > 0 ? deploy.TxIds[0] : null;
        return new Progress(tx, steps.FirstOrDefault(s => s.Seq == 1), false);
    }

    private static string? ResumedDeployTxId(string canonicalJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(canonicalJson);
            return doc.RootElement.TryGetProperty("resumesDeployTxId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<SafeCreateOutcome> StartDeployAsync(Guid conversationId, SafeCreateRequest request, Guid? rebuildOf, CancellationToken ct)
    {
        var build = await builder.BuildDeployAsync(request.Network, request.From, request.Name, ct);
        if (build.Simulation is { Ok: false })
            return SafeCreateOutcome.Error("The safe deployment would fail on chain (simulation), so nothing was previewed. The address may not hold enough ALGO for the fee. Tell the user.");

        var canonical = Canonical(request, build, appId: null);
        var spec = Spec(request, build);
        var snap = rebuildOf is { } old
            ? await previews.RebuildAsync(conversationId, old, canonical, [spec], predictedValueEur: 0m, ct: ct)
            : await previews.CreateAsync(new NewPreviewSpec(conversationId, Capability, canonical, [spec], PredictedValueEur: 0m), ct);
        return new SafeCreateOutcome(SafeCreateState.AwaitingConfirmation,
            $"Step 1 of {TotalSteps} (deploy the safe) is ready for the user's confirmation. Creating a safe is valued at zero and moves no funds. " +
            "When the user says it has landed, call safe.create again with the same arguments: the app id and safe address are read from the chain, " +
            "and step 2 (set up the signer group) is built.",
            snap, request, Simulation: SimulationText(build));
    }

    private async Task<SafeCreateOutcome> ContinueAsync(Guid conversationId, string tenantId, SafeCreateRequest request, ActionPreview preview, CancellationToken ct)
    {
        var snap = await previews.GetAsync(conversationId, preview.Id, null, ct);
        var steps = snap.Steps;

        var progress = Read(preview, steps);
        ulong? appId = progress.DeployTxId is { } tx ? await ResolveAppIdAsync(tenantId, request.Network, tx, ct) : null;

        // While a step is in flight the safe may already be deployed: its app id and address go along when known.
        if (steps.Any(s => s.Status is StepStatus.Submitted or StepStatus.AwaitingConsent or StepStatus.Signed) || preview.Status == ActionPreviewStatus.Confirmed)
            return new SafeCreateOutcome(SafeCreateState.Busy, "A step of this safe creation is being signed or is waiting to land. Wait for it before asking again.", snap, request,
                appId, appId is { } busyId ? SafeAddress(busyId) : null, ResumedDeployTxId: progress.Resume ? progress.DeployTxId : null);
        if (progress.DeployTxId is not null && appId is null)
            return SafeCreateOutcome.Error("The safe was deployed, but its app id could not be read from the chain yet. Tell the user to try again in a moment.");
        var address = appId is { } id ? SafeAddress(id) : null;
        var resumed = progress.Resume ? progress.DeployTxId : null;

        if (progress.Bootstrap is { Status: StepStatus.Confirmed })
            return new SafeCreateOutcome(SafeCreateState.Created,
                $"The safe exists: app id {appId}, safe address {address}. Tell the user both, and that the safe needs ALGO and asset opt-ins before it can hold assets.",
                snap, request, appId, address, ResumedDeployTxId: resumed);

        var pending = steps.FirstOrDefault(s => s.Status == StepStatus.Pending);
        if (pending is not null && preview.Status == ActionPreviewStatus.Proposed && snap.Countdown.CanConfirm)
            return new SafeCreateOutcome(SafeCreateState.AwaitingConfirmation, "The preview is waiting for the user's confirmation.", snap, request, appId, address, ResumedDeployTxId: resumed);

        if (progress.DeployTxId is null)
            return await StartDeployAsync(conversationId, request, preview.Id, ct); // the deploy preview expired: rebuild it

        return await SetUpAsync(request, appId!.Value, snap, resumed, async (spec, build) =>
            preview.Status == ActionPreviewStatus.InProgress && progress.Bootstrap is null && !progress.Resume
                ? await previews.AddNextStepAsync(conversationId, preview.Id, spec, ct)
                : await previews.RebuildAsync(conversationId, preview.Id, Canonical(request, build, appId, resumed), [spec], predictedValueEur: 0m, ct: ct), ct);
    }

    /// <summary>A landed deployment whose setup preview failed or was cancelled: a new preview holding only the setup step.</summary>
    private async Task<SafeCreateOutcome> ResumeAsync(Guid conversationId, string tenantId, SafeCreateRequest request, string deployTxId, CancellationToken ct)
    {
        var appId = await ResolveAppIdAsync(tenantId, request.Network, deployTxId, ct);
        if (appId is null)
            return SafeCreateOutcome.Error("The safe was deployed, but its app id could not be read from the chain yet. Tell the user to try again in a moment.");
        return await SetUpAsync(request, appId.Value, null, deployTxId, (spec, build) =>
            previews.CreateAsync(new NewPreviewSpec(conversationId, Capability, Canonical(request, build, appId, deployTxId), [spec], PredictedValueEur: 0m), ct), ct);
    }

    /// <summary>Funding check, then the bootstrap + finalize step for the deployed safe, persisted by <paramref name="persist"/>.</summary>
    private async Task<SafeCreateOutcome> SetUpAsync(SafeCreateRequest request, ulong appId, PreviewSnapshot? snap, string? resumed,
        Func<NewStepSpec, SafeBuild, Task<PreviewSnapshot>> persist, CancellationToken ct)
    {
        var address = SafeAddress(appId);
        // The setup needs the safe's account funded for its boxes, by a separate transfer.
        var balance = await chain.BalanceAsync(request.Network, address, ct);
        if (balance is null)
            return SafeCreateOutcome.Error("The safe account's balance could not be read. Tell the user to try again in a moment.");
        var floor = Math.Max(_options.MinimumFundingMicroAlgo, balance.MinBalance);
        if (balance.Amount < floor)
            return NeedsFunding(request, appId, address, snap, resumed, RoundUpToTenthAlgo(floor - balance.Amount),
                "Before its signer group can be set up, the safe account needs more ALGO for its storage.");

        var build = await builder.BuildBootstrapAsync(request.Network, request.From, appId, SafeCreateRequest.GroupName, request.Signers, request.Threshold, ct);
        if (build.Simulation is { Ok: false } sim)
        {
            // Only a balance failure is a reason to ask for money; anything else is reported as it is.
            if (IsBalanceFailure(sim.FailureMessage))
                return NeedsFunding(request, appId, address, snap, resumed, _options.MinimumFundingMicroAlgo,
                    "Setting up its signer group would fail on chain because the safe account's balance is too low for its storage.");
            return SafeCreateOutcome.Error(
                $"Setting up the signer group of safe {appId} (address {address}) would fail on chain, so nothing was previewed. Simulation reported: '" +
                Sanitize(sim.FailureMessage) + "'. This is not a funding problem: do not ask the user to send ALGO. Tell the user the setup cannot be prepared right now.");
        }

        var next = await persist(Spec(request, build), build);
        return new SafeCreateOutcome(SafeCreateState.AwaitingConfirmation,
            $"The safe is deployed (app id {appId}, safe address {address}). Step 2 of {TotalSteps} (set up the {request.Describe()} signer group) " +
            "is ready for the user's confirmation. When it has landed, call safe.create again with the same arguments to confirm the safe is ready.",
            next, request, appId, address, Simulation: SimulationText(build), ResumedDeployTxId: resumed);
    }

    private static SafeCreateOutcome NeedsFunding(SafeCreateRequest request, ulong appId, string address, PreviewSnapshot? snap, string? resumed, ulong missing, string why) =>
        new(SafeCreateState.NeedsFunding,
            $"The safe is deployed: app id {appId}, safe address {address}. {why} Tell the user, then call wallet.transfer from {request.From} to {address} for " +
            $"{missing.ToString(CultureInfo.InvariantCulture)} base units of ALGO ({FormatAlgo(missing)} ALGO, assetId 0). After that transfer has landed, call safe.create again with the same arguments.",
            snap, request, appId, address, missing, ResumedDeployTxId: resumed);

    private static bool IsBalanceFailure(string? message) =>
        message is not null && System.Text.RegularExpressions.Regex.IsMatch(message, "balance|overspend|insufficient", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Sidecar and algod text is untrusted: control characters and quotes removed, length bounded.</summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown failure";
        var clean = new string(text.Select(c => char.IsControl(c) || c is '\'' or '"' ? ' ' : c).ToArray()).Trim();
        return clean.Length <= 200 ? clean : clean[..200] + "...";
    }

    /// <summary>
    /// The app id created by the landed deployment: from the tenant's known safes (recorded by the confirm path when the
    /// deployment landed), else, as a fallback, from algod or the indexer (then remembered).
    /// </summary>
    private async Task<ulong?> ResolveAppIdAsync(string tenantId, string network, string txId, CancellationToken ct)
    {
        var known = await db.KnownSafes.AsNoTracking().FirstOrDefaultAsync(k => k.TenantId == tenantId && k.Network == network && k.DeployTxId == txId, ct);
        if (known is not null) return (ulong)known.AppId;

        var appId = await chain.CreatedAppIdAsync(network, txId, ct);
        if (appId is not { } id || id == 0 || id > long.MaxValue) return null;
        await RememberAsync(tenantId, network, id, txId, ct);
        return id;
    }

    private async Task RememberAsync(string tenantId, string network, ulong appId, string txId, CancellationToken ct)
    {
        var row = new KnownSafe { Id = Guid.NewGuid(), TenantId = tenantId, Network = network, AppId = (long)appId, DeployTxId = txId, CreatedAt = clock.GetUtcNow() };
        db.KnownSafes.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Already remembered (a concurrent call): the unique index on (tenant, network, app id) holds it.
            db.Entry(row).State = EntityState.Detached;
        }
    }

    public static string SafeAddress(ulong appId) => Algorand.Address.ForApplication(appId).EncodeAsString();

    private NewStepSpec Spec(SafeCreateRequest request, SafeBuild build) => new(
        ActionStepKind.Sign, build.Group.GroupHash, PreviewPolicy.ExpiryFromLastValid(build.LastValid, build.FirstValid, clock.GetUtcNow()),
        UnsignedGroup: JsonSerializer.Serialize(build.Group.ToBase64()), SenderAddress: request.From, Network: request.StepNetwork,
        FirstValid: (long)build.FirstValid, LastValid: (long)build.LastValid);

    private static string Canonical(SafeCreateRequest request, SafeBuild build, ulong? appId, string? resumesDeployTxId = null)
    {
        var o = new JsonObject
        {
            ["capability"] = Capability,
            ["intent"] = $"Create the {request.Describe()}",
            ["network"] = request.StepNetwork,
            ["sender"] = request.From,
            ["request"] = request.ToJson(),
            ["valueMicroAlgo"] = "0",
            ["totalSteps"] = TotalSteps,
            ["steps"] = new JsonArray("deploy", "bootstrap"),
            ["step"] = build.Step,
            ["groupHash"] = build.Group.GroupHash,
            ["txIds"] = new JsonArray(build.Group.TxIds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["firstValid"] = build.FirstValid.ToString(CultureInfo.InvariantCulture),
            ["lastValid"] = build.LastValid.ToString(CultureInfo.InvariantCulture),
        };
        if (build.ValidatorAppId is { } v) o["validatorAppId"] = v.ToString(CultureInfo.InvariantCulture);
        if (appId is { } id)
        {
            o["appId"] = id.ToString(CultureInfo.InvariantCulture);
            o["safeAddress"] = SafeAddress(id);
        }
        if (resumesDeployTxId is not null)
        {
            // The deployment landed in an earlier preview; this one holds only the setup step.
            o["resumesDeployTxId"] = resumesDeployTxId;
            o["totalSteps"] = 1;
        }
        return JsonCanonicalizer.Canonicalize(o);
    }

    private static string? SimulationText(SafeBuild b) => b.Simulation switch
    {
        null => null,
        { Ok: true } => "Simulation passed",
        _ => "Simulation failed",
    };

    private static ulong RoundUpToTenthAlgo(ulong micro) => (micro + 99_999) / 100_000 * 100_000;

    public static string FormatAlgo(ulong micro)
    {
        var whole = micro / 1_000_000;
        var frac = (micro % 1_000_000).ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        return frac.Length == 0 ? whole.ToString(CultureInfo.InvariantCulture) : whole.ToString(CultureInfo.InvariantCulture) + "." + frac;
    }
}
