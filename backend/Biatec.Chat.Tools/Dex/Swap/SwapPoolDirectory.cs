using System.Net.Http.Json;
using System.Text.Json;
using Biatec.Chat.Tools.Builders;

namespace Biatec.Chat.Tools.Dex.Swap;

/// <summary>Lists the registered Biatec DEX pools of an asset pair (the sidecar's <c>clamm.pools.list</c> query).</summary>
public interface ISwapPoolDirectory
{
    /// <summary>Pools the <paramref name="account"/> may use are in <c>Pools</c>; the rest are in <c>Excluded</c>.</summary>
    Task<SwapPoolListing> ListAsync(string network, ulong assetA, ulong assetB, string account, CancellationToken ct = default);
}

public sealed class SwapPoolDirectoryException(string message) : Exception(message);

/// <summary>Reads pools from the Node builder sidecar over its internal API.</summary>
public sealed class SidecarSwapPoolDirectory(HttpClient http, BuildersOptions options) : ISwapPoolDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SwapPoolListing> ListAsync(string network, ulong assetA, ulong assetB, string account, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseAddress, "v1/query/clamm.pools.list"));
        request.Headers.TryAddWithoutValidation(BuildersOptions.SecretHeader, options.InternalSecret);
        request.Content = JsonContent.Create(new
        {
            network,
            @params = new { assetA = assetA.ToString(), assetB = assetB.ToString(), account },
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new SwapPoolDirectoryException($"Pool query failed ({(int)response.StatusCode}).");
        Body? body;
        try { body = await response.Content.ReadFromJsonAsync<Body>(Json, ct); }
        catch (JsonException) { throw new SwapPoolDirectoryException("Pool query returned invalid JSON."); }
        if (body is null) throw new SwapPoolDirectoryException("Pool query returned nothing.");
        var pools = new List<SwapPool>();
        foreach (var p in body.Pools ?? [])
        {
            if (!ulong.TryParse(p.AppId, out var app) || !ulong.TryParse(p.AssetA, out var a) || !ulong.TryParse(p.AssetB, out var b))
                throw new SwapPoolDirectoryException("Pool query returned an invalid pool.");
            pools.Add(new SwapPool(app, a, b, p.Fee ?? "0", p.FeePercent ?? "0", p.PriceMin ?? "0", p.PriceMax ?? "0",
                p.CurrentPrice ?? "0", p.Liquidity ?? "0", p.VerificationClass));
        }
        var excluded = (body.Excluded ?? [])
            .Where(e => ulong.TryParse(e.AppId, out _))
            .Select(e => new SwapPoolExclusion(ulong.Parse(e.AppId!), e.VerificationClass, e.Reason ?? "verification_class"))
            .ToList();
        return new SwapPoolListing(pools, excluded, body.AccountVerificationClass);
    }

    private sealed record Body(List<PoolBody>? Pools, List<ExcludedBody>? Excluded, int? AccountVerificationClass);
    private sealed record PoolBody(string? AppId, string? AssetA, string? AssetB, string? Fee, string? FeePercent, string? PriceMin,
        string? PriceMax, string? CurrentPrice, string? Liquidity, int VerificationClass);
    private sealed record ExcludedBody(string? AppId, int VerificationClass, string? Reason);
}
