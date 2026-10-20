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
    /// <summary>The first run starts this long after startup, then one run every <see cref="Interval"/>.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>Rows or conversations per statement, so no statement holds long locks.</summary>
    public int BatchSize { get; set; } = 500;
    /// <summary>Each rule keeps draining batches until nothing is left or this much time has passed in the run.</summary>
    public TimeSpan RunBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Evidence payloads and salts, message content and the preview and step payload columns. Not the admin evidence
    /// chain (operator names, reasons, policy values; no end-user data): it keeps its payloads for <see cref="Records"/>.
    /// </summary>
    public TimeSpan Payloads { get; set; } = TimeSpan.FromDays(90);
    /// <summary>Anonymous conversations (no oidcSub and no action previews) are removed after this.</summary>
    public TimeSpan AnonymousConversations { get; set; } = TimeSpan.FromDays(30);
    /// <summary>The chain (entry hashes), receipts and transaction references: 7 years of 365.25 days.</summary>
    public TimeSpan Records { get; set; } = TimeSpan.FromDays(2557);
}
