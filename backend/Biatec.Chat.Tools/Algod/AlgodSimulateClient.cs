using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;

namespace Biatec.Chat.Tools.Algod;

/// <summary>Result of an algod simulate call, reduced to what a preview shows.</summary>
public sealed record SimulationOutcome(
    bool Success,
    string? FailureMessage,
    IReadOnlyList<ulong> FailedAt,
    ulong LastRound,
    ulong? AppBudgetConsumed,
    int TransactionCount)
{
    /// <summary>One line for the preview's simulation summary.</summary>
    public string Summary => Success
        ? $"Simulation succeeded ({TransactionCount} transaction(s), round {LastRound})."
        : $"Simulation failed at {(FailedAt.Count == 0 ? "unknown position" : string.Join("/", FailedAt))}: {FailureMessage}";
}

/// <summary>The two algod calls previews need, so tests (and other networks) can substitute them.</summary>
public interface IAlgodTransport
{
    Task<SimulateResponse> SimulateAsync(SimulateRequest request, CancellationToken ct);
    Task<ulong> GetLastRoundAsync(CancellationToken ct);
}

/// <summary>Real transport over the Algorand .NET SDK's algod client.</summary>
public sealed class SdkAlgodTransport(IDefaultApi api) : IAlgodTransport
{
    public Task<SimulateResponse> SimulateAsync(SimulateRequest request, CancellationToken ct) =>
        api.SimulateTransactionAsync(ct, request, null);

    public async Task<ulong> GetLastRoundAsync(CancellationToken ct) =>
        (await api.TransactionParamsAsync(ct)).LastRound;
}

public interface IAlgodSimulateClient
{
    Task<SimulationOutcome> SimulateAsync(IReadOnlyList<Transaction> unsignedGroup, bool allowUnnamedResources = false, CancellationToken ct = default);
    Task<ulong> GetLastRoundAsync(CancellationToken ct = default);
}

/// <summary>Simulates an unsigned group on one network. Signatures are not needed and no state is changed.</summary>
public sealed class AlgodSimulateClient(IAlgodTransport transport) : IAlgodSimulateClient
{
    public async Task<SimulationOutcome> SimulateAsync(IReadOnlyList<Transaction> unsignedGroup, bool allowUnnamedResources = false, CancellationToken ct = default)
    {
        if (unsignedGroup.Count == 0) throw new GroupException("Nothing to simulate.");
        var request = new SimulateRequest
        {
            AllowEmptySignatures = true,
            // Strict by default: a group missing the references it needs must not show as a success.
            AllowUnnamedResources = allowUnnamedResources,
            TxnGroups =
            [
                new SimulateRequestTransactionGroup
                {
                    Txns = unsignedGroup.Select(t => new SignedTransaction { Tx = t }).ToList(),
                },
            ],
        };
        SimulateResponse response;
        try { response = await transport.SimulateAsync(request, ct); }
        catch (ApiException e) when (e.StatusCode is >= 400 and < 500 and not (401 or 403 or 404 or 408 or 429))
        {
            // algod rejects a malformed group with an HTTP 4xx; that is a failed simulation. Auth errors, 404,
            // throttling, 5xx and outages (timeouts, DNS) propagate so the caller can retry; no raw message is surfaced.
            return new SimulationOutcome(false, $"algod rejected the group (HTTP {e.StatusCode})", [], 0, null, unsignedGroup.Count);
        }
        var group = response.TxnGroups?.FirstOrDefault();
        if (group is null) return new SimulationOutcome(false, "algod returned no simulation result", [], response.LastRound, null, unsignedGroup.Count);
        var failed = !string.IsNullOrEmpty(group.FailureMessage) || (group.FailedAt?.Count ?? 0) > 0;
        return new SimulationOutcome(!failed, group.FailureMessage, group.FailedAt?.ToList() ?? [], response.LastRound,
            group.AppBudgetConsumed, unsignedGroup.Count);
    }

    public Task<ulong> GetLastRoundAsync(CancellationToken ct = default) => transport.GetLastRoundAsync(ct);
}

/// <summary>algod endpoint of one network. The token is read from configuration, never hard-coded.</summary>
public sealed record AlgodNetworkOptions(string BaseUrl, string? Token = null);

/// <summary>One simulate client per network name (for example <c>algorand-mainnet</c>, <c>algorand-testnet</c>).</summary>
public sealed class AlgodClientFactory
{
    private readonly Func<string, IAlgodSimulateClient> _create;
    private readonly Dictionary<string, IAlgodSimulateClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, AlgodNetworkOptions> _networks;
    private readonly object _lock = new();

    public AlgodClientFactory(IReadOnlyDictionary<string, AlgodNetworkOptions> networks, Func<AlgodNetworkOptions, HttpClient>? httpClient = null)
    {
        _networks = new Dictionary<string, AlgodNetworkOptions>(networks, StringComparer.OrdinalIgnoreCase);
        _create = name =>
        {
            var o = _networks[name];
            var http = httpClient?.Invoke(o) ?? new HttpClient { BaseAddress = new Uri(o.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            if (o.Token is { Length: > 0 }) {
                http.DefaultRequestHeaders.Remove("X-Algo-API-Token");
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Algo-API-Token", o.Token);
            }
            if (http.BaseAddress is null) http.BaseAddress = new Uri(o.BaseUrl);
            return new AlgodSimulateClient(new SdkAlgodTransport(new DefaultApi(http)));
        };
    }

    /// <summary>Test seam: supply the client directly.</summary>
    public AlgodClientFactory(IReadOnlyDictionary<string, IAlgodSimulateClient> clients)
    {
        var ci = new Dictionary<string, IAlgodSimulateClient>(clients, StringComparer.OrdinalIgnoreCase);
        _networks = ci.ToDictionary(c => c.Key, _ => new AlgodNetworkOptions(""), StringComparer.OrdinalIgnoreCase);
        _create = name => ci[name];
    }

    public IAlgodSimulateClient For(string network)
    {
        if (!_networks.ContainsKey(network)) throw new ArgumentException($"No algod configured for network '{network}'.", nameof(network));
        lock (_lock)
        {
            if (!_clients.TryGetValue(network, out var c)) _clients[network] = c = _create(network);
            return c;
        }
    }
}
