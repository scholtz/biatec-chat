namespace Biatec.Chat.Api.Retention;

/// <summary>
/// Retention windows, configurable per deployment (PRODUCT-DESCRIPTION.md, Session Recording And Evidence). Defaults:
/// message payloads and their salts 90 days, anonymous conversations 30 days, the chain, receipts and transaction
/// references of fund-moving actions 7 years.
/// </summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";

    /// <summary>Switches the background job off; erasure on request always works.</summary>
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Conversations removed per run and rule, so one run never holds a long transaction.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Evidence payloads and salts, message content and the preview and step payload columns.</summary>
    public TimeSpan Payloads { get; set; } = TimeSpan.FromDays(90);
    /// <summary>Anonymous conversations (no oidcSub and no action previews) are removed after this.</summary>
    public TimeSpan AnonymousConversations { get; set; } = TimeSpan.FromDays(30);
    /// <summary>The chain (entry hashes), receipts and transaction references: 7 years of 365.25 days.</summary>
    public TimeSpan Records { get; set; } = TimeSpan.FromDays(2557);
}
