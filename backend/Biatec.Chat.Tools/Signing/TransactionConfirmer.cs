namespace Biatec.Chat.Tools.Signing;

public enum TxConfirmationState { Confirmed, Pending, Rejected }

/// <summary>Where a submitted transaction stands. <see cref="Round"/> is set when confirmed.</summary>
public sealed record TxConfirmation(TxConfirmationState State, ulong Round = 0, string? Error = null);

/// <summary>Tracks a submitted transaction until it is confirmed, rejected or the wait runs out.</summary>
public interface ITransactionConfirmer
{
    Task<TxConfirmation> WaitAsync(string network, string txId, CancellationToken ct = default);
}
