using System.Globalization;
using System.Text;

namespace Biatec.Chat.Tools;

/// <summary>
/// The one cleaner for text that comes from outside the user (asset creators, algod, sidecars, chain data) before it
/// is shown on a card or put in model-facing text. It is data, never instructions.
/// </summary>
public static class UntrustedText
{
    /// <summary>
    /// Every non-printing character (controls, Unicode format characters such as bidi overrides and zero-width
    /// characters, line/paragraph separators, private-use, unassigned and lone surrogates), quotes, backticks, angle
    /// brackets and backslashes become spaces; whitespace runs collapse to one space; the result is cut to
    /// <paramref name="max"/> characters (with "..." when <paramref name="ellipsis"/>). Empty when nothing is left.
    /// </summary>
    public static string Clean(string? text, int max = 200, bool ellipsis = true)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes()) // a lone surrogate enumerates as U+FFFD, kept out below
        {
            var drop = rune == Rune.ReplacementChar || Rune.IsWhiteSpace(rune) || IsNonPrinting(Rune.GetUnicodeCategory(rune))
                       || rune.Value is '\'' or '"' or '`' or '<' or '>' or '\\';
            if (drop) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
            else sb.Append(rune.ToString());
        }
        var clean = sb.ToString().Trim();
        if (clean.Length <= max) return clean;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(clean[cut - 1])) cut--; // never split a surrogate pair
        return clean[..cut].TrimEnd() + (ellipsis ? "..." : "");
    }

    private static bool IsNonPrinting(UnicodeCategory c) => c is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse
        or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate;
}
