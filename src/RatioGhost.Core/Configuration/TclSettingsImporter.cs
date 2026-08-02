using System.Globalization;
using System.Text;

namespace RatioGhost.Core.Configuration;

public static class TclSettingsImporter
{
    public static IReadOnlyDictionary<string, string> ParseArrayList(string input)
    {
        var words = ParseList(input);
        if (words.Count % 2 != 0)
            throw new FormatException("The Tcl settings list must contain key/value pairs.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < words.Count; index += 2)
        {
            var key = words[index];
            if (key.Length is 0 or > 128)
                throw new FormatException("A Tcl setting key is empty or too long.");
            values[key] = words[index + 1];
        }
        return values;
    }

    public static RatioGhostSettings Map(
        IReadOnlyDictionary<string, string> values,
        RatioGhostSettings defaults)
    {
        return defaults with
        {
            ListenPort = ReadInt(values, "listen_port", defaults.ListenPort),
            OnlyTrackerTraffic = ReadBool(values, "only_tracker", defaults.OnlyTrackerTraffic),
            OnlyLocalConnections = ReadBool(values, "only_local", defaults.OnlyLocalConnections),
            ProxyDebugLogging = ReadBool(values, "proxy_debug_logging", defaults.ProxyDebugLogging),
            StartMinimized = ReadBool(values, "start_minimized", defaults.StartMinimized),
            AutoStart = ReadBool(values, "autostart", defaults.AutoStart),
            MinimumPeers = ReadInt(values, "min_peers", defaults.MinimumPeers),
            UploadPerDownloadMinimum = ReadDouble(values, "updown_ratio_a", defaults.UploadPerDownloadMinimum),
            UploadPerDownloadMaximum = ReadDouble(values, "updown_ratio_b", defaults.UploadPerDownloadMaximum),
            UploadPerUploadMinimum = ReadDouble(values, "upup_ratio_a", defaults.UploadPerUploadMinimum),
            UploadPerUploadMaximum = ReadDouble(values, "upup_ratio_b", defaults.UploadPerUploadMaximum),
            BoostKiBPerSecond = ReadDouble(values, "boost", defaults.BoostKiBPerSecond),
            BoostChancePercent = ReadInt(values, "boost_chance", defaults.BoostChancePercent),
            ReportDownloadAsZero = ReadBool(values, "no_download", defaults.ReportDownloadAsZero),
            PretendToSeed = ReadBool(values, "seed", defaults.PretendToSeed),
            LifetimeRuntimeSeconds = ReadLong(values, "runtime", defaults.LifetimeRuntimeSeconds),
            LifetimeActualDownloaded = ReadLong(values, "actual_down", defaults.LifetimeActualDownloaded),
            LifetimeActualUploaded = ReadLong(values, "actual_up", defaults.LifetimeActualUploaded),
            LifetimeReportedDownloaded = ReadLong(values, "reported_down", defaults.LifetimeReportedDownloaded),
            LifetimeReportedUploaded = ReadLong(values, "reported_up", defaults.LifetimeReportedUploaded),
            Sessions = ReadInt(values, "sessions", defaults.Sessions)
        };
    }

    private static IReadOnlyList<string> ParseList(string input)
    {
        var words = new List<string>();
        var index = 0;
        while (true)
        {
            SkipWhitespace(input, ref index);
            if (index >= input.Length)
                break;
            words.Add(input[index] switch
            {
                '{' => ReadBraced(input, ref index),
                '"' => ReadQuoted(input, ref index),
                _ => ReadBare(input, ref index)
            });
            if (index < input.Length && !char.IsWhiteSpace(input[index]))
                throw new FormatException("Unexpected characters after a Tcl list element.");
        }
        return words;
    }

    private static string ReadBraced(string input, ref int index)
    {
        index++;
        var depth = 1;
        var output = new StringBuilder();
        while (index < input.Length)
        {
            var character = input[index++];
            if (character == '\\' && index < input.Length &&
                (input[index] == '\r' || input[index] == '\n'))
            {
                ConsumeLineContinuation(input, ref index);
                output.Append(' ');
                continue;
            }
            if (character == '{')
            {
                depth++;
                output.Append(character);
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                    return output.ToString();
                output.Append(character);
            }
            else
            {
                output.Append(character);
            }
        }
        throw new FormatException("Unterminated braced Tcl list element.");
    }

    private static string ReadQuoted(string input, ref int index)
    {
        index++;
        var output = new StringBuilder();
        while (index < input.Length)
        {
            var character = input[index++];
            if (character == '"')
                return output.ToString();
            if (character == '\\')
                output.Append(ReadBackslash(input, ref index));
            else
                output.Append(character);
        }
        throw new FormatException("Unterminated quoted Tcl list element.");
    }

    private static string ReadBare(string input, ref int index)
    {
        var output = new StringBuilder();
        while (index < input.Length && !char.IsWhiteSpace(input[index]))
        {
            var character = input[index++];
            output.Append(character == '\\' ? ReadBackslash(input, ref index) : character);
        }
        return output.ToString();
    }

    private static string ReadBackslash(string input, ref int index)
    {
        if (index >= input.Length)
            throw new FormatException("Trailing backslash in Tcl list.");
        var character = input[index++];
        if (character is '\r' or '\n')
        {
            index--;
            ConsumeLineContinuation(input, ref index);
            return " ";
        }
        return character switch
        {
            'a' => "\a",
            'b' => "\b",
            'f' => "\f",
            'n' => "\n",
            'r' => "\r",
            't' => "\t",
            'v' => "\v",
            'x' => ReadHex(input, ref index, maximumDigits: 2),
            'u' => ReadHex(input, ref index, maximumDigits: 4),
            _ when character is >= '0' and <= '7' => ReadOctal(input, ref index, character),
            _ => character.ToString()
        };
    }

    private static string ReadHex(string input, ref int index, int maximumDigits)
    {
        var start = index;
        while (index < input.Length && index - start < maximumDigits && Uri.IsHexDigit(input[index]))
            index++;
        if (index == start)
            return maximumDigits == 2 ? "x" : "u";
        var value = int.Parse(input[start..index], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return char.ConvertFromUtf32(value);
    }

    private static string ReadOctal(string input, ref int index, char first)
    {
        var value = first - '0';
        var digits = 1;
        while (index < input.Length && digits < 3 && input[index] is >= '0' and <= '7')
        {
            value = value * 8 + input[index++] - '0';
            digits++;
        }
        return char.ConvertFromUtf32(value);
    }

    private static void ConsumeLineContinuation(string input, ref int index)
    {
        if (input[index] == '\r')
        {
            index++;
            if (index < input.Length && input[index] == '\n')
                index++;
        }
        else
        {
            index++;
        }
        while (index < input.Length && input[index] is ' ' or '\t')
            index++;
    }

    private static void SkipWhitespace(string input, ref int index)
    {
        while (index < input.Length && char.IsWhiteSpace(input[index]))
            index++;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool fallback)
    {
        if (!values.TryGetValue(key, out var text))
            return fallback;
        return text switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new FormatException($"Invalid Tcl boolean setting: {key}.")
        };
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback) =>
        !values.TryGetValue(key, out var text)
            ? fallback
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"Invalid Tcl integer setting: {key}.");

    private static long ReadLong(
        IReadOnlyDictionary<string, string> values,
        string key,
        long fallback) =>
        !values.TryGetValue(key, out var text)
            ? fallback
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"Invalid Tcl integer setting: {key}.");

    private static double ReadDouble(
        IReadOnlyDictionary<string, string> values,
        string key,
        double fallback) =>
        !values.TryGetValue(key, out var text)
            ? fallback
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"Invalid Tcl numeric setting: {key}.");
}
