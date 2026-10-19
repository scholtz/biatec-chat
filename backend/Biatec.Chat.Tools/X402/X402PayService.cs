using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Biatec.Chat.Tools.X402;

/// <summary>The USDC transfer as decoded from the built transaction, never from the seller's advertised price.</summary>
public sealed record DecodedTransfer(string Sender, string Receiver, ulong AssetId, ulong Amount);

/// <summary>A built, unsigned x402 payment ready to preview.</summary>
public sealed record X402Preparation(
    string Network,
    string ResourceUrl,
    X402Requirements Requirements,
    GroupedTransactions Group,
    DecodedTransfer Transfer);

public sealed record X402PayResult(string Transaction, string Content);

public sealed class X402Exception(string code, string message) : Exception(message)
{
    /// <summary>Machine-readable reason, safe to show.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// The buyer side of x402. <see cref="PrepareAsync"/> fetches the payment requirements (SSRF-policed), has the builder
/// sidecar build the USDC transfer, and decodes what that group really does. <see cref="PayAsync"/> takes the signed
/// group, has the facilitator verify and settle it, and then fetches the resource with the payment attached.
/// Signing is not here: the backend signs only through Biatec OIDC, after the user confirms the preview.
/// </summary>
public sealed class X402PayService(
    X402HostPolicy policy,
    HttpClient resourceHttp,
    IBuildersClient builders,
    IX402Facilitator facilitator,
    X402Options options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The USDC ASA per network; x402.pay pays in nothing else.</summary>
    public static readonly IReadOnlyDictionary<string, ulong> UsdcAssetIds = new Dictionary<string, ulong>(StringComparer.Ordinal)
    {
        ["mainnet"] = 31566704UL,
        ["testnet"] = 10458941UL,
    };

    public async Task<X402Preparation> PrepareAsync(string network, string from, string resourceUrl, CancellationToken ct)
    {
        if (!options.Enabled) throw new X402Exception("feature_disabled", "x402 payments are not enabled.");
        var uri = await policy.CheckAsync(resourceUrl, ct);

        var requirements = await FetchRequirementsAsync(uri, network, ct);
        if (!UsdcAssetIds.TryGetValue(network, out var usdc) || requirements.Asset != usdc)
            throw new X402Exception("asset_not_allowed", "Only USDC payments are supported.");
        if (requirements.MaxAmountRequired == 0 || requirements.MaxAmountRequired > options.MaxAmountBaseUnits)
            throw new X402Exception("amount_not_allowed", "The advertised price is outside the allowed range.");

        BuildResult built;
        try
        {
            built = await builders.BuildAsync("x402", network, new
            {
                from,
                payTo = requirements.PayTo,
                assetId = requirements.Asset.ToString(),
                amount = requirements.MaxAmountRequired.ToString(),
                resourceUrl = uri.AbsoluteUri,
            }, ct);
        }
        catch (BuilderException e)
        {
            throw new X402Exception("build_failed", e.PublicMessage);
        }

        GroupedTransactions group;
        DecodedTransfer transfer;
        try
        {
            var decoded = AlgorandGroup.Decode(built.Transactions);
            RequireNoHiddenFields(built.Transactions, decoded);
            group = AlgorandGroup.Ensure(decoded);
            transfer = DecodeTransfer(group);
        }
        catch (Exception e) when (e is GroupException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new X402Exception("build_invalid", "The builder returned a payment that could not be decoded.");
        }

        // The decoded group is the truth. If it is not exactly what was asked for, refuse rather than preview something else.
        if (transfer.Sender != from || transfer.Receiver != requirements.PayTo || transfer.AssetId != requirements.Asset
            || transfer.Amount > requirements.MaxAmountRequired || transfer.Amount > options.MaxAmountBaseUnits)
            throw new X402Exception("build_mismatch", "The built payment does not match the payment requirements.");

        return new X402Preparation(network, uri.AbsoluteUri, requirements, group, transfer);
    }

    /// <summary>Only a single asset transfer is a valid x402 payment; anything else in the group is refused.</summary>
    internal static DecodedTransfer DecodeTransfer(GroupedTransactions group)
    {
        if (group.Transactions.Count != 1 || group.Transactions[0] is not AssetTransferTransaction axfer)
            throw new X402Exception("build_mismatch", "The payment must be exactly one asset transfer.");
        if (axfer.RekeyTo is not null && !IsZero(axfer.RekeyTo)) throw new X402Exception("build_mismatch", "The payment must not rekey.");
        return new DecodedTransfer(axfer.Sender.EncodeAsString(), axfer.AssetReceiver.EncodeAsString(), axfer.XferAsset, axfer.AssetAmount);
    }

    /// <summary>
    /// The SDK model has no field for asset close-out or clawback, so decoding would silently drop them. Re-encoding the
    /// decoded transaction must give back the builder's exact bytes; any extra field makes them differ and the payment is refused.
    /// </summary>
    internal static void RequireNoHiddenFields(IReadOnlyList<string> base64, IReadOnlyList<Transaction> decoded)
    {
        for (var i = 0; i < decoded.Count; i++)
            if (!Algorand.Utils.Encoder.EncodeToMsgPackOrdered(decoded[i]).AsSpan().SequenceEqual(Convert.FromBase64String(base64[i])))
                throw new X402Exception("build_mismatch", "The built payment contains fields that cannot be verified.");
    }

    private static bool IsZero(Address a) => a.Bytes.All(b => b == 0);

    public async Task<X402PayResult> PayAsync(X402Preparation preparation, IReadOnlyList<string> signedGroup, CancellationToken ct)
    {
        if (!options.Enabled) throw new X402Exception("feature_disabled", "x402 payments are not enabled.");
        var uri = await policy.CheckAsync(preparation.ResourceUrl, ct);
        var payment = new X402PaymentPayload(signedGroup);

        var verified = await facilitator.VerifyAsync(payment, preparation.Requirements, ct);
        if (!verified.IsValid) throw new X402Exception("verify_failed", "The facilitator did not accept the payment.");
        var settled = await facilitator.SettleAsync(payment, preparation.Requirements, ct);
        if (!settled.Success || string.IsNullOrEmpty(settled.Transaction)) throw new X402Exception("settle_failed", "The payment could not be settled.");

        var header = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            x402Version = 1,
            scheme = preparation.Requirements.Scheme,
            network = preparation.Requirements.Network,
            payload = new { paymentGroup = signedGroup, paymentIndex = 0 },
        }, Json));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-PAYMENT", header);
        var (status, body) = await SendAsync(request, ct);
        if (status != HttpStatusCode.OK) throw new X402Exception("resource_failed", "The paid payment was settled but the resource did not return content.");
        return new X402PayResult(settled.Transaction!, body);
    }

    private async Task<X402Requirements> FetchRequirementsAsync(Uri uri, string network, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var (status, body) = await SendAsync(request, ct);
        if (status != HttpStatusCode.PaymentRequired)
            throw new X402Exception("not_payable", "The resource did not ask for an x402 payment.");
        return ParseRequirements(body, network, uri);
    }

    internal static X402Requirements ParseRequirements(string body, string network, Uri resource)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("accepts", out var accepts) || accepts.ValueKind != JsonValueKind.Array)
                throw new X402Exception("bad_requirements", "The payment requirements were not understood.");
            foreach (var a in accepts.EnumerateArray())
            {
                var scheme = Str(a, "scheme");
                var net = Str(a, "network");
                if (scheme != "exact" || net is null || !net.Contains("algorand", StringComparison.OrdinalIgnoreCase)
                    || !net.Contains(network, StringComparison.OrdinalIgnoreCase)) continue;
                var payTo = Str(a, "payTo");
                if (payTo is null || !Algorand.Address.IsValid(payTo) || !TryUlong(a, "maxAmountRequired", out var amount) || !TryUlong(a, "asset", out var asset) || asset == 0) continue;
                var description = Str(a, "description");
                if (description is { Length: > 200 }) description = description[..200];
                return new X402Requirements(scheme, net, amount, resource.AbsoluteUri, payTo, asset, description);
            }
        }
        catch (JsonException) { }
        throw new X402Exception("bad_requirements", "The resource has no payment option this assistant can use.");
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryUlong(JsonElement o, string name, out ulong value)
    {
        value = 0;
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.String ? ulong.TryParse(v.GetString(), System.Globalization.NumberStyles.None, null, out value)
            : v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out value);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await resourceHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // Redirects are never followed: a 3xx could point at an internal address.
            if ((int)response.StatusCode is >= 300 and < 400) throw new X402Exception("redirect_refused", "The resource redirected, which is not followed.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var bytes = await X402Http.ReadBoundedAsync(stream, options.MaxResponseBytes, ct);
            return (response.StatusCode, Encoding.UTF8.GetString(bytes));
        }
        catch (HttpRequestException e) when (e.InnerException is X402PolicyException p) { throw p; }
        catch (HttpRequestException) { throw new X402Exception("resource_unreachable", "The resource could not be reached."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new X402Exception("resource_unreachable", "The resource timed out."); }
    }
}

public static class X402ServiceCollectionExtensions
{
    public const string ResourceClientName = "x402-resource";

    /// <summary>
    /// Registers x402. The resource client never follows redirects, ignores proxies and re-checks the resolved address when
    /// the socket connects. The facilitator is the HTTP one when <c>X402:FacilitatorUrl</c> is set, else the mock, which
    /// moves no funds. Binds <c>X402</c> options; <c>X402:Enabled</c> is false by default.
    /// </summary>
    public static IServiceCollection AddX402(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var options = Microsoft.Extensions.Configuration.ConfigurationBinder.Get<X402Options>(configuration.GetSection(X402Options.Section)) ?? new X402Options();
        services.AddSingleton(options);
        services.AddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<X402HostPolicy>();
        services.AddHttpClient(ResourceClientName, c => c.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var policy = sp.GetRequiredService<X402HostPolicy>();
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                    ConnectCallback = async (ctx, ct) =>
                    {
                        var addresses = await policy.RequirePublicAsync(ctx.DnsEndPoint.Host, ct);
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    },
                };
            });
        if (!string.IsNullOrWhiteSpace(options.FacilitatorUrl))
            services.AddHttpClient<IX402Facilitator, HttpX402Facilitator>(c => c.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds));
        else
            services.AddSingleton<IX402Facilitator, MockX402Facilitator>();
        services.AddTransient(sp => new X402PayService(
            sp.GetRequiredService<X402HostPolicy>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ResourceClientName),
            sp.GetRequiredService<Builders.IBuildersClient>(),
            sp.GetRequiredService<IX402Facilitator>(),
            options));
        return services;
    }
}
