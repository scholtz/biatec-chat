namespace Biatec.Chat.Core.Previews;

/// <summary>Timing rules for action previews (PRODUCT-DESCRIPTION.md, "Action Preview").</summary>
public static class PreviewPolicy
{
    /// <summary>Confirm is disabled, and refused by the backend, below this many remaining seconds.</summary>
    public const int MinConfirmSeconds = 600;

    /// <summary>Algorand allows a validity window of at most 1000 rounds.</summary>
    public const ulong MaxValidRounds = 1000;

    /// <summary>
    /// Conservative seconds per round (mainnet is about 2.8; 1000 rounds is about 45 minutes). Using the lower
    /// figure makes the computed expiry earlier than the real one, never later.
    /// </summary>
    public const double SecondsPerRound = 2.7;

    /// <summary>A Bitcoin preview expires after this long (T-024 adds the input-spent and fee-rate triggers).</summary>
    public static readonly TimeSpan BitcoinLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Wall-clock expiry of a group whose last valid round is <paramref name="lastValid"/>, given the chain's
    /// current round. A group already past its last valid round expires at <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset ExpiryFromLastValid(ulong lastValid, ulong currentRound, DateTimeOffset now)
    {
        if (lastValid <= currentRound) return now;
        var rounds = Math.Min(lastValid - currentRound, MaxValidRounds);
        return now.AddSeconds(Math.Floor(rounds * SecondsPerRound));
    }

    /// <summary>
    /// Seconds left on the countdown: the smaller of the group's remaining validity and, when given, the session's
    /// remaining absolute lifetime. Never negative.
    /// </summary>
    public static int RemainingSeconds(DateTimeOffset expiresAt, DateTimeOffset now, DateTimeOffset? sessionAbsoluteExpiry = null)
    {
        var end = sessionAbsoluteExpiry is { } s && s < expiresAt ? s : expiresAt;
        var seconds = Math.Floor((end - now).TotalSeconds);
        return seconds <= 0 ? 0 : seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
    }

    public static bool CanConfirm(int remainingSeconds) => remainingSeconds >= MinConfirmSeconds;
}
