using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Evidence.Export;
using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Api.Onboarding;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Admin;

/// <summary>
/// Admin API settings (section <c>Admin</c>). Operators authenticate with named keys (<see cref="Operators"/>); the
/// shared <c>Onboarding:OperatorKey</c> is still accepted as the deprecated operator <c>legacy</c>.
/// </summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Admin requests per window and remote IP (default 60), counted apart from public onboarding.</summary>
    public int RateLimitPermits { get; set; } = 60;

    /// <summary>Length of the rate-limit window in seconds (default 60).</summary>
    public int RateLimitWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Named operators: <c>Admin:Operators:&lt;name&gt;:KeyHash</c> is the hex SHA-256 of that operator's bearer key. Only
    /// the hash is configured (environment or secret store); the key itself is never in configuration or the repo.
    /// </summary>
    public Dictionary<string, OperatorKeyOptions> Operators { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Operator admin API for tenant policy and knowledge-base pages (docs/admin-api.md). It lives under <c>/onboarding/operator/</c> on purpose:
/// that prefix already is the operator surface, so it inherits the bearer-only posture of the operator approval route
/// (no cookie, so exempt from the CSRF guard; never a CORS wildcard; Authorization is not an allowed CORS header, so a
/// browser page cannot send the bearer cross-origin), plus the onboarding body size limit. Every handler authenticates
/// (<see cref="OperatorAuth"/>) before it reads the body; the API is disabled (404) while no operator key is set. Every
/// committed change is attributed to the named operator and appended to the admin evidence chain (<see cref="AdminEvidence"/>).
/// </summary>
public static class AdminEndpoints
{
    public const string RateLimitPolicy = "operator";

    public const string EvidencePath = "/onboarding/operator/evidence";
    public const int DefaultEvidenceLimit = 200;
    public const int MaxEvidenceLimit = 1000;

    public static IServiceCollection AddAdmin(this IServiceCollection services, IConfiguration config)
    {
        // A malformed or shared operator key hash stops the start (naming the entry and rule) instead of silently
        // locking an operator out. The keyring decodes the hashes once.
        services.AddOptions<AdminOptions>().Bind(config.GetSection(AdminOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminOptions>, AdminOptionsValidator>();
        services.AddSingleton<OperatorKeyring>();
        services.AddScoped<TenantPolicyService>();
        services.AddScoped<KbAdminService>();
        services.AddRateLimiter(o => o.AddPolicy(RateLimitPolicy, ctx =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter("operator:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, opts.RateLimitPermits),
                Window = TimeSpan.FromSeconds(Math.Max(1, opts.RateLimitWindowSeconds)),
                QueueLimit = 0,
            });
        }));
        return services;
    }

    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/onboarding/operator/tenants").RequireRateLimiting(RateLimitPolicy);

        g.MapGet("/", async (HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var list = await svc.ListAsync(ct);
            log.LogInformation("Admin: operator {Operator} from {RemoteIp} listed {Count} tenant policies", OperatorAuth.Current(ctx).Name, OperatorAuth.Remote(ctx), list.Count);
            return Results.Json(new { tenants = list });
        });

        g.MapGet("/{tenantId}", async (string tenantId, HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var view = await svc.GetAsync(tenantId, ct);
            log.LogInformation("Admin: operator {Operator} from {RemoteIp} read tenant policy {TenantId} (found: {Found})",
                OperatorAuth.Current(ctx).Name, OperatorAuth.Remote(ctx), OperatorAuth.Clean(tenantId), view is not null);
            return view is null ? Results.Json(new { error = "Unknown tenant." }, statusCode: 404) : Results.Json(view);
        });

        // The body is bound by hand after authentication, so an unauthenticated request never reaches the JSON parser.
        g.MapMethods("/{tenantId}/policy", [HttpMethods.Patch], async (string tenantId, HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var op = OperatorAuth.Current(ctx);
            var id = OperatorAuth.Clean(tenantId);
            var remote = OperatorAuth.Remote(ctx);
            if (!ctx.Request.HasJsonContentType())
                return Results.Json(new { error = "Content-Type must be application/json." }, statusCode: 415);
            TenantPolicyPatch? patch;
            try { patch = await ctx.Request.ReadFromJsonAsync<TenantPolicyPatch>(ct); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                log.LogWarning("Admin: refused tenant policy change {TenantId} by operator {Operator} from {RemoteIp}: unreadable body", id, op.Name, remote);
                return Results.Json(new { error = "The body must be a JSON policy patch with known fields only." }, statusCode: 400);
            }

            var r = await svc.UpdateAsync(tenantId, patch, op, ct);
            var reason = TenantPolicyService.CleanReason(patch?.Reason);
            switch (r.Status)
            {
                case PolicyStatus.Ok:
                    // Audit record: policy fields and values only; the request never carries a secret. The same change is in the admin evidence chain.
                    log.LogInformation("Admin: operator {Operator} from {RemoteIp} changed tenant policy {TenantId}: {Changes}. Reason: {Reason}",
                        op.Name, remote, id, r.Changes is { Count: > 0 } c ? string.Join("; ", c) : "(no change)", reason);
                    return Results.Json(new { tenant = r.View, changes = r.Changes });
                case PolicyStatus.NotFound:
                    log.LogWarning("Admin: operator {Operator} from {RemoteIp} tried to change unknown tenant {TenantId}", op.Name, remote, id);
                    return Results.Json(new { error = r.Error }, statusCode: 404);
                default:
                    log.LogWarning("Admin: refused tenant policy change {TenantId} by operator {Operator} from {RemoteIp}: {Error}. Reason: {Reason}", id, op.Name, remote, r.Error, reason);
                    return Results.Json(new { error = r.Error }, statusCode: 400);
            }
        });

        // One page of the admin evidence chain as a signed evidence bundle (the conversation export format, so the offline
        // verifier checks it), with the server's verdict over the same snapshot.
        app.MapGet(EvidencePath, async (HttpContext ctx, ChatDbContext db, IEvidenceKeyStore keys, TimeProvider clock, IConfiguration config,
            ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            // Query read by hand after authentication, so a malformed one never answers before the auth check.
            long from = 1;
            int take = DefaultEvidenceLimit;
            if ((ctx.Request.Query.TryGetValue("fromSeq", out var f) && !long.TryParse(f, out from)) ||
                (ctx.Request.Query.TryGetValue("limit", out var l) && !int.TryParse(l, out take)))
                return Results.Json(new { error = "fromSeq and limit must be integers." }, statusCode: 400);
            from = Math.Clamp(from, 1, long.MaxValue / 2);
            take = Math.Clamp(take, 1, MaxEvidenceLimit);
            var last = from + take - 1;

            // One read: every entry from seq 1 to the end of the page, plus one more to tell whether a next page exists.
            // The verdict covers seq 1..throughSeq of this snapshot, so it matches the entries returned.
            var prefix = await db.EvidenceEntries.AsNoTracking()
                .Where(e => e.ConversationId == AdminEvidence.ChainId && e.Seq <= last + 1)
                .OrderBy(e => e.Seq).ToListAsync(ct);
            var hasMore = prefix.Count > 0 && prefix[^1].Seq > last;
            if (hasMore) prefix.RemoveAt(prefix.Count - 1);
            var map = (await keys.GetPublicKeysAsync(ct)).ToDictionary(k => k.Kid);
            var v = prefix.Count == 0 ? EvidenceVerification.Ok : EvidenceVerifier.Verify(prefix, map);
            var page = prefix.Where(e => e.Seq >= from).ToList();

            var publicBase = config["Evidence:PublicBaseUrl"]?.Trim().TrimEnd('/');
            var jwksUrl = (string.IsNullOrEmpty(publicBase) ? $"{ctx.Request.Scheme}://{ctx.Request.Host}" : publicBase) + EvidenceJwks.JwksPath;
            var bundle = await EvidenceBundleBuilder.BuildAsync(AdminEvidence.ChainId, page, keys, jwksUrl, clock.GetUtcNow(), ct);

            log.LogInformation("Admin: operator {Operator} from {RemoteIp} read the admin evidence chain (seq {From}, {Count} entries, valid: {Valid})",
                OperatorAuth.Current(ctx).Name, OperatorAuth.Remote(ctx), from, page.Count, v.Valid);
            var body = new JsonObject
            {
                ["chainId"] = AdminEvidence.ChainId.ToString("D"),
                ["fromSeq"] = from,
                ["limit"] = take,
                ["nextFromSeq"] = hasMore ? last + 1 : null,
                ["verification"] = new JsonObject
                {
                    ["valid"] = v.Valid,
                    ["throughSeq"] = prefix.Count == 0 ? 0 : prefix[^1].Seq,
                    ["failedSeq"] = v.FailedSeq,
                    ["error"] = v.Error,
                },
                ["bundle"] = bundle,
            };
            return Results.Content(body.ToJsonString(), "application/json");
        }).RequireRateLimiting(RateLimitPolicy);

        MapKb(app);
        return app;
    }

    /// <summary>The header that carries the audit reason for a knowledge-base change (the body is the page itself).</summary>
    public const string ReasonHeader = "X-Admin-Reason";

    private static void MapKb(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/onboarding/operator/kb").RequireRateLimiting(RateLimitPolicy);

        g.MapGet("/pages", async (HttpContext ctx, KbAdminService svc, ILogger<KbAdminService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var (uploaded, served) = await svc.ListAsync(ct);
            log.LogInformation("Admin: operator {Operator} from {RemoteIp} listed kb pages ({Uploaded} uploaded, {Served} served)",
                OperatorAuth.Current(ctx).Name, OperatorAuth.Remote(ctx), uploaded.Count, served.Count);
            return Results.Json(new { uploaded, served });
        });

        // The raw body is the page. It is read by hand after authentication, bounded by Kb:MaxPageBytes.
        g.MapPut("/pages/{pageId}", async (string pageId, HttpContext ctx, KbAdminService svc, ILogger<KbAdminService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var op = OperatorAuth.Current(ctx);
            var id = OperatorAuth.Clean(pageId);
            var remote = OperatorAuth.Remote(ctx);
            var reason = TenantPolicyService.CleanReason(ctx.Request.Headers[ReasonHeader].ToString());
            if (reason is null)
                return Refuse(log, op, remote, id, null, 400, $"{ReasonHeader} is required: 1-200 characters, no control characters.");
            if (KbAdminService.AcceptedType(ctx.Request.ContentType) is null)
                return Refuse(log, op, remote, id, reason, 415, "Content-Type must be text/markdown or text/plain (UTF-8).");
            if (ctx.Request.ContentLength > svc.MaxPageBytes)
                return Refuse(log, op, remote, id, reason, 413, $"A page is at most {svc.MaxPageBytes} bytes.");

            byte[] body;
            try { body = await ReadBoundedAsync(ctx.Request.Body, svc.MaxPageBytes + 1, ct); }
            catch (BadHttpRequestException) { return Refuse(log, op, remote, id, reason, 413, $"A page is at most {svc.MaxPageBytes} bytes."); }

            var r = await svc.UploadAsync(pageId, ctx.Request.ContentType, body, op, reason, ct);
            switch (r.Change)
            {
                case KbChange.Created or KbChange.Replaced or KbChange.Unchanged:
                    // Audit record, written once the change is committed: id, size and hash only, never the content.
                    log.LogInformation("Admin: operator {Operator} from {RemoteIp} uploaded kb page {PageId} ({Change}, {Bytes} bytes, sha256 {Sha256}); {Reload}. Reason: {Reason}",
                        op.Name, remote, id, r.Change.ToString().ToLowerInvariant(), r.Page!.SizeBytes, r.Page.Sha256, ReloadNote(r), reason);
                    return Results.Json(new { page = r.Page, change = r.Change.ToString().ToLowerInvariant(), served = r.Served, reloaded = r.ReloadError is null, note = Note(r) },
                        statusCode: r.Change == KbChange.Created ? 201 : 200);
                case KbChange.UnsupportedType: return Refuse(log, op, remote, id, reason, 415, r.Error!);
                case KbChange.TooLarge: return Refuse(log, op, remote, id, reason, 413, r.Error!);
                default: return Refuse(log, op, remote, id, reason, 400, r.Error!);
            }
        });

        g.MapDelete("/pages/{pageId}", async (string pageId, HttpContext ctx, KbAdminService svc, ILogger<KbAdminService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var op = OperatorAuth.Current(ctx);
            var id = OperatorAuth.Clean(pageId);
            var remote = OperatorAuth.Remote(ctx);
            var reason = TenantPolicyService.CleanReason(ctx.Request.Headers[ReasonHeader].ToString());
            if (reason is null)
                return Refuse(log, op, remote, id, null, 400, $"{ReasonHeader} is required: 1-200 characters, no control characters.");
            var r = await svc.DeleteAsync(pageId, op, reason, ct);
            if (r.Change == KbChange.NotFound)
            {
                log.LogWarning("Admin: operator {Operator} from {RemoteIp} tried to delete unknown kb page {PageId}", op.Name, remote, id);
                return Results.Json(new { error = r.Error }, statusCode: 404);
            }
            log.LogInformation("Admin: operator {Operator} from {RemoteIp} deleted kb page {PageId}; {Reload}. Reason: {Reason}", op.Name, remote, id, ReloadNote(r), reason);
            return Results.Json(new { deleted = pageId, served = r.Served, reloaded = r.ReloadError is null, note = Note(r) });
        });

        // Re-reads the seed directory (Kb:Path) and the uploaded pages on this replica. X-Admin-Reason is optional here.
        g.MapPost("/reload", async (HttpContext ctx, KbLibrary library, IEvidenceLog evidence, ILogger<KbAdminService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var op = OperatorAuth.Current(ctx);
            var remote = OperatorAuth.Remote(ctx);
            string? reason = null;
            if (ctx.Request.Headers.ContainsKey(ReasonHeader) && (reason = TenantPolicyService.CleanReason(ctx.Request.Headers[ReasonHeader].ToString())) is null)
                return Results.Json(new { error = $"{ReasonHeader}, when sent, must be 1-200 characters, no control characters." }, statusCode: 400);

            var r = await library.ReloadAsync(reseed: true, ct);
            // A reload stores nothing and has already run, so it is recorded afterwards and best-effort: with the database
            // down the operator still gets the diagnosable 503 below, and the missing entry is logged as an error.
            try
            {
                // Not the request token: a client that disconnects after the reload ran must not skip its record.
                await AdminEvidence.AppendAsync(evidence, op, AdminEvidence.KbReload, "kb", reason, null,
                    new JsonObject { ["served"] = r.Served, ["partial"] = !r.Ok }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Admin: the kb reload by operator {Operator} from {RemoteIp} is not in the admin evidence chain: {Error}",
                    op.Name, remote, KbLibrary.Describe(ex));
            }
            if (!r.Ok)
            {
                // Partial: the new seed is served with the last uploaded pages read; the database could not be read.
                log.LogWarning("Admin: operator {Operator} from {RemoteIp} reloaded the knowledge base partially: seed reloaded, uploaded pages unavailable ({Error}); serving {Served} pages. Reason: {Reason}",
                    op.Name, remote, r.Error, r.Served, reason);
                return Results.Json(new { served = r.Served, partial = true, error = "Seed pages reloaded; the uploaded pages could not be read, so the last ones read are served. " + r.Error }, statusCode: 503);
            }
            log.LogInformation("Admin: operator {Operator} from {RemoteIp} reloaded the knowledge base; serving {Served} pages. Reason: {Reason}", op.Name, remote, r.Served, reason);
            return Results.Json(new { served = r.Served, partial = false });
        });
    }

    private static string ReloadNote(KbWriteResult r) => r.Change == KbChange.Unchanged
        ? $"no reload needed, serving {r.Served} pages"
        : r.ReloadError is null ? $"serving {r.Served} pages" : $"stored, this replica could not reload ({r.ReloadError}) and picks it up on its next refresh";

    private static string? Note(KbWriteResult r) =>
        r.ReloadError is null ? null : "Stored; this replica will pick it up on the next refresh.";

    private static IResult Refuse(ILogger log, OperatorIdentity op, string remote, string id, string? reason, int status, string error)
    {
        log.LogWarning("Admin: refused kb page change {PageId} by operator {Operator} from {RemoteIp}: {Error}. Reason: {Reason}", id, op.Name, remote, error, reason);
        return Results.Json(new { error }, statusCode: status);
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; a longer body is cut there, so the caller sees it as too large.</summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while (ms.Length < limit && (n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - ms.Length)), ct)) > 0)
            ms.Write(buffer, 0, n);
        return ms.ToArray();
    }
}
