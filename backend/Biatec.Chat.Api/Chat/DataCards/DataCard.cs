namespace Biatec.Chat.Api.Chat.DataCards;

/// <summary>One asset row of a holdings card.</summary>
public sealed record DataCardAsset
{
    /// <summary>Asset id as a decimal string ("0" is ALGO); for Bitcoin the symbol.</summary>
    public required string AssetId { get; init; }
    /// <summary>Unit name from the chain or the tool result: untrusted text.</summary>
    public required string UnitName { get; init; }
    /// <summary>Amount as a decimal string, never a number, so values above 2^53 keep every digit.</summary>
    public required string Amount { get; init; }
    public required int Decimals { get; init; }
    /// <summary>Fiat value as a decimal string; absent when no price source is available.</summary>
    public string? FiatValue { get; init; }
}
