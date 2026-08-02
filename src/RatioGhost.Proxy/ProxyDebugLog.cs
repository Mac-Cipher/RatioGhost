using System.Text.RegularExpressions;

namespace RatioGhost.Proxy;

public interface IProxyDebugLogger
{
    void Write(string message);
}

/// <summary>
/// Redacts tracker credentials and token-like path segments before a message
/// can reach a diagnostic sink.
/// </summary>
public static partial class ProxyDebugRedactor
{
    [GeneratedRegex(
        "(?<key>info_hash|passkey|authkey|token|peer_id|key|ip|ipv4|ipv6)=(?<value>[^&\\s]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryPattern();

    [GeneratedRegex(
        "(?<prefix>/)[A-Za-z0-9._~-]{20,}(?=(/|[?\\s]|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePathPattern();

    public static string RedactSensitive(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = SensitiveQueryPattern().Replace(text, "${key}=<redacted>");
        return SensitivePathPattern().Replace(redacted, "/<redacted>");
    }
}

/// <summary>
/// Best-effort rotating file logger. Diagnostics must never interrupt proxy
/// traffic, so all filesystem failures are intentionally swallowed.
/// </summary>
public sealed class FileProxyDebugLogger : IProxyDebugLogger
{
    private const long MaximumBytes = 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new();

    public FileProxyDebugLogger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrWhiteSpace(directory))
                    return;
                Directory.CreateDirectory(directory);
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaximumBytes)
                    File.Move(_path, _path + ".1", overwrite: true);

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}: " +
                           ProxyDebugRedactor.RedactSensitive(message) +
                           Environment.NewLine;
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception)
        {
            // Debug logging is strictly optional and must never disrupt proxy
            // traffic because a profile directory can be read-only or locked.
        }
    }
}
