using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RatioGhost.Core.Announcements;

public sealed record TrackerResponse(int? Complete, int? Incomplete, int? Interval, string? FailureReason);

public static partial class TrackerResponseParser
{
    public static TrackerResponse Parse(ReadOnlySpan<byte> payload)
    {
        var text = Encoding.Latin1.GetString(payload);
        return new TrackerResponse(
            ReadInteger(text, "complete"),
            ReadInteger(text, "incomplete"),
            ReadInteger(text, "interval"),
            ReadFailureReason(text));
    }

    private static int? ReadInteger(string text, string field)
    {
        var match = Regex.Match(text, $"{field.Length}:{Regex.Escape(field)}i([0-9]+)e",
            RegexOptions.CultureInvariant);
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }

    private static string? ReadFailureReason(string text)
    {
        var match = FailureLengthRegex().Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var length) ||
            length > 4096)
            return null;

        var marker = $"14:failure reason{length}:";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0 || start + marker.Length + length > text.Length)
            return null;
        return text.Substring(start + marker.Length, length);
    }

    [GeneratedRegex("14:failure reason([0-9]{1,5}):", RegexOptions.CultureInvariant)]
    private static partial Regex FailureLengthRegex();
}
