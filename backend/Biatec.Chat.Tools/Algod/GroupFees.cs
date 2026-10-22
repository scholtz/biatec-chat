namespace Biatec.Chat.Tools.Algod;

/// <summary>
/// Per-transaction fee ceilings for groups a builder (not the backend) assembled. The fee is an ALGO outflow the card does not
/// show per transaction, so a builder must not be able to pick it freely (BCHAT-003).
/// </summary>
public static class GroupFees
{
    /// <summary>Payments and asset transfers: ten times the minimum fee. Matches <c>TransferStepBuilder.MaxFee</c>.</summary>
    public const ulong MaxTransferFeeMicroAlgo = 10_000;

    /// <summary>
    /// Application calls: they may pay for inner transactions and opcode budget (the CLAMM library uses static fees of 8,000 for
    /// add and 12,000 for remove liquidity), so the ceiling is higher but still far below anything that would drain a balance.
    /// </summary>
    public const ulong MaxCallFeeMicroAlgo = 100_000;

    public static bool TooHigh(ulong? fee, bool isApplicationCall) => fee > (isApplicationCall ? MaxCallFeeMicroAlgo : MaxTransferFeeMicroAlgo);
}
