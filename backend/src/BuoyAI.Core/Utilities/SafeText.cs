namespace BuoyAI.Core.Utilities;

/// <summary>
/// Length cuts that never split a UTF-16 surrogate pair. Emoji and some other characters take
/// two UTF-16 chars (a surrogate pair). A plain <c>s[..n]</c> can keep only the first half, and
/// Npgsql's strict UTF-8 encoder then throws when the text is saved, failing the whole save.
/// </summary>
public static class SafeText
{
    /// <summary>
    /// Returns at most <paramref name="maxLength"/> chars of <paramref name="value"/>. If the
    /// cut would fall inside a surrogate pair, the whole pair is dropped instead. (It keeps pairs
    /// whole, not grapheme clusters: a combining mark or a joined emoji sequence can still be
    /// cut between its parts, which is valid text and saves fine.)
    /// </summary>
    public static string Truncate(string value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (value.Length <= maxLength)
        {
            return value;
        }

        var end = maxLength;
        if (end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    /// <summary>
    /// Shortens <paramref name="value"/> to fit <paramref name="maxLength"/> with a trailing
    /// "...", the way chat titles are shown. Text that already fits is returned unchanged. A
    /// limit under 3 leaves no room for the dots, so the text is just cut.
    /// </summary>
    public static string Ellipsize(string value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (value.Length <= maxLength)
        {
            return value;
        }

        return maxLength < 3 ? Truncate(value, maxLength) : Truncate(value, maxLength - 3) + "...";
    }
}
