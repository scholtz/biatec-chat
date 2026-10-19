using System.Globalization;
using System.Text.RegularExpressions;
using Biatec.Chat.Api.Security;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Onboarding;

public sealed record OnboardingRequest
{
    public string? Host { get; init; }
    public string? SignInPageUrl { get; init; }
    public string[]? AllowedOrigins { get; init; }
    public string[]? ClientKeys { get; init; }
}

public sealed record OperatorApprovalRequest
{
    public OnboardingRequest? Request { get; init; }
    public string? OidcClientId { get; init; }
    /// <summary>A <c>secret://</c> pointer into the secret store; never the secret.</summary>
    public string? ClientSecretRef { get; init; }
}

public sealed record ChallengeRecord(string Host, string RecordType, string RecordName, string RecordValue);

public enum OnboardingStatus { Ok, Created, PendingOperator, Invalid, HostTaken, Unverified, Disabled, Failed }

public sealed record OnboardingResult(OnboardingStatus Status, string? Error = null, object? Body = null);

public sealed partial class OnboardingService(
    ChatDbContext db,
    IDnsTxtResolver dns,
    IOidcClientRegistrar registrar,
    ISecretStore secrets,
    IOptions<OnboardingOptions> options,
    ILogger<OnboardingService> log)
{
    private sealed record Validated(string Host, string? SignInPageUrl, string[] Origins, string[] ClientKeys)
    {
        /// <summary>Canonical digest of the requested configuration; the DNS challenge is bound to it.</summary>
        public string ConfigDigest => Challenge.Digest(Host, SignInPageUrl, Origins, ClientKeys);
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^secret://[A-Za-z0-9][A-Za-z0-9._/-]{0,200}$")]
    private static partial Regex SecretRefRegex();

    /// <summary>A lower-case ASCII DNS name with at least two labels and no IP literal, or null.</summary>
    public static string? NormalizeHostName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string ascii;
        try { ascii = new IdnMapping().GetAscii(raw.Trim().TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
        if (ascii.Length is 0 or > 230) return null; // leaves room for the challenge label within 253
        var labels = ascii.Split('.');
        if (labels.Length < 2 || !labels.All(l => LabelRegex().IsMatch(l))) return null;
        if (labels.All(l => l.All(char.IsAsciiDigit))) return null; // IPv4 literal
        return ascii;
    }

    private static string HostOfOrigin(string canonicalOrigin) => new Uri(canonicalOrigin).IdnHost;

    private OnboardingResult? Validate(OnboardingRequest? req, out Validated? v)
    {
        v = null;
        if (string.IsNullOrEmpty(options.Value.ChallengeKey))
            return new(OnboardingStatus.Disabled, "Tenant onboarding is not configured on this deployment.");
        var host = NormalizeHostName(req?.Host);
        if (host is null) return new(OnboardingStatus.Invalid, "host must be a DNS name with at least two labels.");

        if ((req!.AllowedOrigins?.Length ?? 0) > 10 || (req.ClientKeys?.Length ?? 0) > 20)
            return new(OnboardingStatus.Invalid, "At most 10 allowedOrigins and 20 clientKeys.");
        var origins = new List<string>();
        foreach (var raw in req.AllowedOrigins ?? [])
        {
            var o = OriginPolicy.Canonicalize(raw);
            if (o is null || !o.StartsWith("https://", StringComparison.Ordinal))
                return new(OnboardingStatus.Invalid, $"allowedOrigins entry '{raw}' must be a bare https origin.");
            var oh = NormalizeHostName(HostOfOrigin(o));
            if (oh is null) return new(OnboardingStatus.Invalid, $"allowedOrigins entry '{raw}' must use a DNS name.");
            if (!RegistrableDomain.SameSite(oh, host))
                return new(OnboardingStatus.Invalid,
                    $"Origin {o} is not same-site with {host} (different registrable domain). The session cookie could never be first-party there, so it is rejected; " +
                    "use an origin under the same registrable domain.");
            if (!origins.Contains(o)) origins.Add(o);
        }
        if (origins.Count > 10) return new(OnboardingStatus.Invalid, "At most 10 allowedOrigins.");

        string? signIn = null;
        if (!string.IsNullOrWhiteSpace(req.SignInPageUrl))
        {
            var s = OriginPolicy.Canonicalize(req.SignInPageUrl.TrimEnd('/'));
            if (s is null || !origins.Contains(s))
                return new(OnboardingStatus.Invalid, "signInPageUrl must be a bare https origin equal to one of the registered allowedOrigins.");
            signIn = s;
        }

        var keys = (req.ClientKeys is { Length: > 0 } ? req.ClientKeys : [host]).Select(k => k?.Trim() ?? "").Distinct().ToArray();
        if (keys.Length > 20 || keys.Any(k => k.Length is 0 or > 64 || !k.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':')))
            return new(OnboardingStatus.Invalid, "clientKeys must be 1-20 labels of at most 64 characters from [A-Za-z0-9._:-].");

        v = new Validated(host, signIn, origins.ToArray(), keys);
        return null;
    }

    /// <summary>Hosts that need their own TXT proof: the backend host, and every origin host that is neither it nor beneath it.</summary>
    private static List<string> ProofHosts(Validated v)
    {
        var hosts = new List<string> { v.Host };
        foreach (var o in v.Origins)
        {
            var oh = HostOfOrigin(o).ToLowerInvariant();
            var covered = oh == v.Host || oh.EndsWith("." + v.Host, StringComparison.Ordinal);
            if (!covered && !hosts.Contains(oh)) hosts.Add(oh);
        }
        return hosts;
    }

    public async Task<OnboardingResult> GetChallengesAsync(OnboardingRequest? req, CancellationToken ct)
    {
        if (Validate(req, out var v) is { } bad) return bad;
        if (await db.Tenants.AnyAsync(t => t.Host == v!.Host, ct)) return HostTaken(v!.Host);
        var key = options.Value.ChallengeKey!;
        var records = ProofHosts(v!).Select(h => new ChallengeRecord(h, "TXT", Challenge.RecordName(h), Challenge.Value(key, h, v.ConfigDigest))).ToArray();
        return new(OnboardingStatus.Ok, Body: new { challenges = records });
    }

    private static OnboardingResult HostTaken(string host) =>
        new(OnboardingStatus.HostTaken, $"Host {host} already belongs to a tenant.");

    private async Task<string[]> UnverifiedAsync(Validated v, CancellationToken ct)
    {
        var key = options.Value.ChallengeKey!;
        var hosts = ProofHosts(v);
        var digest = v.ConfigDigest;
        var lookups = await Task.WhenAll(hosts.Select(h => dns.GetTxtRecordsAsync(Challenge.RecordName(h), ct)));
        return hosts.Where((h, i) => !Challenge.Matches(key, h, digest, lookups[i])).ToArray();
    }

    /// <summary>Self-service: verify the proofs, then register a confidential client or fall back to operator approval.</summary>
    public async Task<OnboardingResult> OnboardAsync(OnboardingRequest? req, CancellationToken ct)
    {
        if (Validate(req, out var v) is { } bad) return bad;
        if (await db.Tenants.AnyAsync(t => t.Host == v!.Host, ct)) return HostTaken(v!.Host);

        var missing = await UnverifiedAsync(v!, ct);
        if (missing.Length > 0)
            return new(OnboardingStatus.Unverified, "DNS TXT challenge not found for: " + string.Join(", ", missing), new { unverified = missing });

        // Serialize concurrent onboardings of one host (transaction-scoped advisory lock), so a lost race never
        // registers a second client or overwrites the winner's secret.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"biatec-chat-onboarding:" + v!.Host}, 0))", ct);
        if (await db.Tenants.AnyAsync(t => t.Host == v.Host, ct)) return HostTaken(v.Host);

        var tenantId = v.Host;
        var redirectUri = $"https://{v.Host}/auth/callback";
        RegisteredClient? client;
        try { client = await registrar.RegisterAsync($"Biatec Chat ({v.Host})", redirectUri, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Dynamic client registration failed for {Host}", v.Host);
            return new(OnboardingStatus.Failed, "Client registration failed; try again later.");
        }

        if (client is null)
        {
            // Fallback: the proofs hold, an operator adds the static client and approves through the operator endpoint.
            return new(OnboardingStatus.PendingOperator, "Domain verified. An operator must add the OIDC client and approve the tenant.",
                new { host = v.Host, status = "pending-operator-approval", redirectUri });
        }

        string secretRef;
        try { secretRef = await secrets.PutAsync($"tenants/{tenantId}/oidc-client-secret", client.ClientSecret, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Storing the client secret failed for {Host}", v.Host);
            return new(OnboardingStatus.Failed, "Could not store the client secret; try again later.");
        }
        var created = await CreateTenantAsync(v, tenantId, client.ClientId, secretRef, redirectUri, ct);
        if (created.Status == OnboardingStatus.Created) await tx.CommitAsync(ct);
        return created;
    }

    /// <summary>Operator fallback: the proofs are re-checked, the operator supplies the static client and a secret reference.</summary>
    public async Task<OnboardingResult> ApproveAsync(OperatorApprovalRequest? req, CancellationToken ct)
    {
        if (Validate(req?.Request, out var v) is { } bad) return bad;
        var clientId = req!.OidcClientId?.Trim();
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 128 || clientId.Any(char.IsControl))
            return new(OnboardingStatus.Invalid, "oidcClientId is required.");
        if (req.ClientSecretRef is null || !SecretRefRegex().IsMatch(req.ClientSecretRef) || req.ClientSecretRef["secret://".Length..].Split('/').Any(seg => seg is "" or "." or ".."))
            return new(OnboardingStatus.Invalid, "clientSecretRef must be a secret:// reference into the secret store, never the secret itself.");
        if (await db.Tenants.AnyAsync(t => t.Host == v!.Host, ct)) return HostTaken(v!.Host);
        if (await db.Tenants.AnyAsync(t => t.ClientSecretRef == req.ClientSecretRef, ct))
            return new(OnboardingStatus.Invalid, "clientSecretRef is already used by another tenant.");

        var missing = await UnverifiedAsync(v!, ct);
        if (missing.Length > 0)
            return new(OnboardingStatus.Unverified, "DNS TXT challenge not found for: " + string.Join(", ", missing), new { unverified = missing });

        return await CreateTenantAsync(v!, v!.Host, clientId, req.ClientSecretRef, $"https://{v.Host}/auth/callback", ct);
    }

    private async Task<OnboardingResult> CreateTenantAsync(Validated v, string tenantId, string clientId, string secretRef, string redirectUri, CancellationToken ct)
    {
        var tenant = new Tenant
        {
            Id = tenantId,
            Host = v.Host,
            SignInPageUrl = v.SignInPageUrl,
            OidcClientId = clientId,
            ClientSecretRef = secretRef,
            RedirectUri = redirectUri,
            AllowedOrigins = v.Origins,
            ClientKeys = System.Text.Json.JsonSerializer.Serialize(v.ClientKeys),
            // Platform defaults; a self-registered tenant can never loosen them and is not trust-anchor verified.
            ConsentThresholdCeilingEur = 1000,
            SoftCapEurPerHour = 1500,
            TrustAnchorVerified = false,
        };
        db.Tenants.Add(tenant);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // The unique index on Host (or the primary key) decides races between concurrent onboardings.
            db.Entry(tenant).State = EntityState.Detached;
            if (await db.Tenants.AnyAsync(t => t.Host == v.Host, ct)) return HostTaken(v.Host);
            throw;
        }
        return new(OnboardingStatus.Created, Body: new
        {
            tenantId, host = v.Host, status = "active", oidcClientId = clientId, clientSecretRef = secretRef, redirectUri, allowedOrigins = v.Origins,
        });
    }
}
