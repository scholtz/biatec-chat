using System.Globalization;
using System.Text.Json;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.X402;
using Microsoft.Extensions.DependencyInjection;

namespace Biatec.Chat.Api.X402;

/// <summary>
/// Settles a confirmed, signed x402 payment: the facilitator verifies and settles the group (this is what broadcasts it),
/// then the resource is fetched with the payment attached. The requirements come from the preview's stored canonical JSON,
/// i.e. exactly what the user confirmed, never from the seller again.
/// </summary>
public sealed class X402SettlementHandler(IServiceScopeFactory scopes) : ISettlementHandler
{
    public string Capability => "x402.pay";

    public async Task<SettlementResult> SettleAsync(ActionPreview preview, ActionStep step, IReadOnlyList<string> signedGroupBase64, CancellationToken ct)
    {
        X402Requirements requirements;
        string resourceUrl;
        try
        {
            using var doc = JsonDocument.Parse(preview.CanonicalJson);
            var r = doc.RootElement;
            resourceUrl = r.GetProperty("resourceUrl").GetString()!;
            requirements = X402PayService.RequirementsFor(r.GetProperty("network").GetString()!, resourceUrl, r.GetProperty("payTo").GetString()!,
                ulong.Parse(r.GetProperty("assetId").GetString()!, CultureInfo.InvariantCulture), ulong.Parse(r.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        {
            return new SettlementResult(SettlementKind.NotBroadcast, "The stored payment could not be read. Nothing was paid; the preview must be rebuilt.", ErrorCode: "x402_preview_invalid");
        }

        using var scope = scopes.CreateScope();
        var pay = scope.ServiceProvider.GetRequiredService<X402PayService>();
        try
        {
            var paid = await pay.PayAsync(requirements, resourceUrl, signedGroupBase64, ct);
            return new SettlementResult(SettlementKind.Settled, "The payment settled and the resource was delivered.", paid.Content);
        }
        catch (X402Exception e) when (e.Code is "verify_failed" or "feature_disabled")
        {
            return new SettlementResult(SettlementKind.NotBroadcast, $"The payment was not accepted ({e.Code}). Nothing was paid; the preview must be rebuilt.", ErrorCode: e.Code);
        }
        catch (X402PolicyException e)
        {
            return new SettlementResult(SettlementKind.NotBroadcast, $"The resource cannot be used ({e.Code}). Nothing was paid.", ErrorCode: e.Code);
        }
        catch (X402Exception e) when (e.Code == "resource_failed")
        {
            // Settled, but the seller returned no content: the money moved, so the step is not retried.
            return new SettlementResult(SettlementKind.Settled, e.Message, ErrorCode: e.Code);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // settle_failed, an unreachable facilitator or a transport error: the group may have been broadcast.
            return new SettlementResult(SettlementKind.Unknown, "The payment could not be confirmed either way; it may still settle. Check again shortly.", ErrorCode: (e as X402Exception)?.Code);
        }
    }
}
