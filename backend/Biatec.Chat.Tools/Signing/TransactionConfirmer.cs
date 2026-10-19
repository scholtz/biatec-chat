namespace Biatec.Chat.Tools.Signing;

public enum TxConfirmationState { Confirmed, Pending, Rejected, Expired }

/// <summary>Where a submitted transaction stands. <see cref="Round"/> is set when confirmed.</summary>
public sealed record TxConfirmation(TxConfirmationState State, ulong Round = 0, string? Error = null);

/// <summary>Tracks a submitted transaction until it is confirmed, rejected or the wait runs out.</summary>
public interface ITransactionConfirmer
{
    /// <remarks>
    /// <paramref name="lastValid"/> is the last round the group can land in (0 if unknown). When the transaction is not
    /// found and the network is already past it, the state is <see cref="TxConfirmationState.Expired"/>: it can never land.
    /// </remarks>
    Task<TxConfirmation> WaitAsync(string network, string txId, ulong lastValid, CancellationToken ct = default);
}
