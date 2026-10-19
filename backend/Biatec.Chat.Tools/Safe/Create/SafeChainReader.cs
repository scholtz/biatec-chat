namespace Biatec.Chat.Tools.Safe.Create;

/// <summary>An account's ALGO balance and minimum balance, in microAlgo.</summary>
public sealed record SafeAccountBalance(ulong Amount, ulong MinBalance);

/// <summary>The two chain reads safe creation needs between its steps. Implemented over algod (and the indexer) in the API.</summary>
public interface ISafeChainReader
{
    /// <summary>The application id the confirmed deployment transaction created, or null when it cannot be found.</summary>
    Task<ulong?> CreatedAppIdAsync(string network, string txId, CancellationToken ct = default);

    /// <summary>The account's balance, or null when it cannot be read.</summary>
    Task<SafeAccountBalance?> BalanceAsync(string network, string address, CancellationToken ct = default);
}
