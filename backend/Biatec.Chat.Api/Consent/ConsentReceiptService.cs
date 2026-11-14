using System.Security.Cryptography;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using ActionStep = Biatec.Chat.Core.Data.ActionStep;
using Biatec.Chat.Tools.Consent;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Biatec.Chat.Api.Consent;

/// <summary>Consent settings of the backend (<c>Consent</c> section).</summary>
public sealed class ConsentOptions
{
    public const string Section = "Consent";

    /// <summary>
    /// Verify the signed receipt of every consent before the action resumes (T-238). Always on in production; false exists only so a broken
    /// deployment can be diagnosed, and is logged as a warning at startup.
    /// </summary>
    public bool VerifyReceipts { get; set; } = true;
}

/// <summary>Logs a warning at startup when receipt verification is switched off.</summary>
public sealed class ConsentVerificationStartupCheck(IOptions<ConsentOptions> options, ILogger<ConsentVerificationStartupCheck> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.VerifyReceipts)
            log.LogWarning("Consent:VerifyReceipts is false: consent receipts are NOT verified. Use this only to diagnose a broken deployment.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A denial receipt that was fetched and checked: the receipt text (null if it could not be had) and the verdict of its verification.</summary>
public sealed record DenialReceipt(string? Receipt, string FailureCode)
{
    public bool Verified => Receipt is not null && FailureCode == ReceiptVerdict.Valid.Code();
}

/// <summary>
/// Verifies consent receipts for the orchestrator (T-238) against the stored request and preview, with the Biatec OIDC JWKS the backend already
/// loads through discovery (refetched once on an unknown <c>kid</c>). Never throws; a failure to get the keys is a refusal.
/// </summary>
public sealed class ConsentReceiptService(IOidcMetadataProvider metadata, IConsentReceiptClient client, ITokenVault vault, IOptions<OidcOptions> oidc,
    IOptions<ConsentOptions> options, TimeProvider clock, ILogger<ConsentReceiptService> log)
{
    /// <summary>The denial fetch is best effort: it must never hold up cancelling the action.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    public bool Enabled => options.Value.VerifyReceipts;

    /// <summary>The receipt of an approval, verified for the approval path: decision approve, the stored code, not expired.</summary>
    public Task<ReceiptVerdict> VerifyApprovalAsync(Tenant tenant, ChatSession session, ConsentRequest request, ActionStep step, string receipt, CancellationToken ct) =>
        VerifyAsync(tenant, session, request, step, receipt, ["approve"], request.BindingCode ?? "", checkExpiry: true, ct);

    /// <summary>
    /// The receipt of a denial (<c>deny</c> or <c>code_failed</c>): fetched with the session's access token and verified like an approval except that
    /// the typed code and the expiry are not held against a denial. Best effort and bounded; never throws.
    /// </summary>
    public async Task<DenialReceipt> FetchDenialAsync(Tenant tenant, ChatSession session, ConsentRequest request, ActionStep step, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);
            var bearer = await vault.GetAccessTokenAsync(session.Id.ToString("N"), timeout.Token);
            if (string.IsNullOrEmpty(bearer)) return new DenialReceipt(null, "no_session_token");
            var endpoint = new Uri(oidc.Value.Issuer.TrimEnd('/') + "/consent/receipts/" + Uri.EscapeDataString(request.AuthReqId));
            var receipt = await client.GetAsync(endpoint, bearer, timeout.Token);
            if (receipt is null) return new DenialReceipt(null, "receipt_unavailable");
            var verdict = await VerifyAsync(tenant, session, request, step, receipt, ["deny", "code_failed"], bindingCode: null, checkExpiry: false, timeout.Token);
            return new DenialReceipt(receipt, verdict.Code());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning("The denial receipt could not be fetched or verified: {ErrorType}", ex.GetType().Name);
            return new DenialReceipt(null, "receipt_unavailable");
        }
    }

    private async Task<ReceiptVerdict> VerifyAsync(Tenant tenant, ChatSession session, ConsentRequest request, ActionStep step, string receipt,
        IReadOnlyCollection<string> decisions, string? bindingCode, bool checkExpiry, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(step.SubjectHash) || step.Network is null || step.SenderAddress is null) return ReceiptVerdict.DetailsMismatch;
            // Everything below is what the backend itself asked for: the stored request, the session and the previewed group.
            var expected = new ConsentReceiptExpectation(
                Issuer: oidc.Value.Issuer, Now: clock.GetUtcNow(), AuthReqId: request.AuthReqId, ClientEntityId: tenant.ClientEntityId, Sub: session.OidcSub ?? "",
                BindingCode: bindingCode, BindingMessage: "", GroupHash: ConsentReceiptVerifier.GroupHash(System.Convert.FromHexString(step.SubjectHash)),
                Network: step.Network, Sender: step.SenderAddress, Origin: ConfirmService.ReportedOrigin(tenant, request.Origin), Decisions: decisions, CheckExpiry: checkExpiry);

            var verdict = ConsentReceiptVerifier.Verify(receipt, ToSigningKeys(await metadata.GetSigningKeysAsync(false, ct)), expected);
            // A key the cache does not know may be a rotation: refetch once (the provider rate limits it) and try again.
            if (verdict == ReceiptVerdict.AttestationUnknownKey)
                verdict = ConsentReceiptVerifier.Verify(receipt, ToSigningKeys(await metadata.GetSigningKeysAsync(true, ct)), expected);
            return verdict;
        }
        catch (Exception ex) when (ex is OidcException or FormatException)
        {
            log.LogWarning("Receipt verification could not get the Biatec OIDC signing keys: {ErrorType}", ex.GetType().Name);
            return ReceiptVerdict.KeysUnavailable;
        }
    }

    /// <summary>The EC P-256 keys of a JWKS as the verifier takes them. Keys of another type are left out: they can never verify an attestation.</summary>
    internal static IReadOnlyList<OidcSigningKey> ToSigningKeys(IReadOnlyList<SecurityKey> keys)
    {
        var result = new List<OidcSigningKey>();
        foreach (var key in keys)
        {
            switch (key)
            {
                case JsonWebKey { Kid: { Length: > 0 } kid, Kty: "EC", X: { } x, Y: { } y } jwk:
                    result.Add(new OidcSigningKey(kid, "EC", jwk.Crv ?? "", string.IsNullOrEmpty(jwk.Use) ? null : jwk.Use, x, y));
                    break;
                case ECDsaSecurityKey { KeyId: { Length: > 0 } kid } ec:
                    var p = ec.ECDsa.ExportParameters(false);
                    if (p.Q.X is { } qx && p.Q.Y is { } qy && p.Curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256")
                        result.Add(new OidcSigningKey(kid, "EC", "P-256", null, Base64UrlEncoder.Encode(qx), Base64UrlEncoder.Encode(qy)));
                    break;
            }
        }
        return result;
    }
}
