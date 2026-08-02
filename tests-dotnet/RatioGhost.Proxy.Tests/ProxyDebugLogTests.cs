namespace RatioGhost.Proxy.Tests;

public sealed class ProxyDebugLogTests
{
    [Fact]
    public void Redactor_RemovesTrackerSecretsAndLongPathTokens()
    {
        var message =
            "GET /announce/1234567890abcdef1234567890abcdef?info_hash=abc&passkey=secret&uploaded=12";

        var redacted = ProxyDebugRedactor.RedactSensitive(message);

        Assert.Contains("/announce/<redacted>", redacted, StringComparison.Ordinal);
        Assert.Contains("info_hash=<redacted>", redacted, StringComparison.Ordinal);
        Assert.Contains("passkey=<redacted>", redacted, StringComparison.Ordinal);
        Assert.Contains("uploaded=12", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890abcdef1234567890abcdef", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void FileLogger_RotatesAndNeverWritesUnredactedSecrets()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.ProxyDebugLogTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "proxy_debug.log");
        try
        {
            var logger = new FileProxyDebugLogger(path);
            logger.Write(new string('x', 1024 * 1024));
            logger.Write("passkey=secret");

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));
            Assert.Contains("passkey=<redacted>", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.DoesNotContain("secret", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
