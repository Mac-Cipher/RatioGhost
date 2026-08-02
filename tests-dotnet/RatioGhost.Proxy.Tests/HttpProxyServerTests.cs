using System.Net;
using System.Net.Sockets;
using System.Text;
using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;

namespace RatioGhost.Proxy.Tests;

public sealed class HttpProxyServerTests
{
    [Fact]
    public async Task Proxy_RewritesAndForwardsHttpAnnounce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tracker = new TcpListener(IPAddress.Loopback, 0);
        tracker.Start();
        var trackerPort = ((IPEndPoint)tracker.LocalEndpoint).Port;
        var proxyPort = ReservePort();
        var observedRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackerTask = RunTrackerAsync(tracker, observedRequest, timeout.Token);
        var debugLog = new CapturingDebugLogger();

        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings
            {
                ListenPort = proxyPort,
                ReportDownloadAsZero = true,
                PretendToSeed = true
            },
            debugLogger: debugLog,
            isDebugLogging: () => true);
        await proxy.StartAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await using var stream = client.GetStream();
        var absoluteTarget =
            $"http://127.0.0.1:{trackerPort}/announce?info_hash=abc&passkey=secret&downloaded=50&uploaded=20&left=700";
        var request = Encoding.ASCII.GetBytes(
            $"GET {absoluteTarget} HTTP/1.1\r\nHost: 127.0.0.1:{trackerPort}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, timeout.Token);
        using var responseBuffer = new MemoryStream();
        await stream.CopyToAsync(responseBuffer, timeout.Token);

        var trackerRequest = await observedRequest.Task.WaitAsync(timeout.Token);
        Assert.Contains("downloaded=0", trackerRequest, StringComparison.Ordinal);
        Assert.Contains("uploaded=20", trackerRequest, StringComparison.Ordinal);
        Assert.Contains("left=0", trackerRequest, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(responseBuffer.ToArray()), StringComparison.Ordinal);
        var announceLog = Assert.Single(
            debugLog.Messages,
            message => message.Contains("Announce statistics rewritten", StringComparison.Ordinal));
        Assert.Contains("passkey=<redacted>", announceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", announceLog, StringComparison.Ordinal);

        await proxy.StopAsync();
        await trackerTask;
    }

    [Fact]
    public async Task Proxy_DoesNotWriteDebugLogWhenDisabled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var debugLog = new CapturingDebugLogger();
        var proxyPort = ReservePort();
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings { ListenPort = proxyPort },
            debugLogger: debugLog,
            isDebugLogging: () => false);

        await proxy.StartAsync(timeout.Token);
        Assert.Empty(debugLog.Messages);
        await proxy.StopAsync();
    }

    [Fact]
    public async Task Proxy_FailsClosedForConnectUntilCertificateAuthorityIsImplemented()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var proxyPort = ReservePort();
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings { ListenPort = proxyPort });
        await proxy.StartAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes("CONNECT tracker.test:443 HTTP/1.1\r\nHost: tracker.test:443\r\n\r\n"),
            timeout.Token);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, timeout.Token);

        Assert.StartsWith("HTTP/1.1 501", Encoding.ASCII.GetString(response.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CONNECT tracker.test HTTP/1.1\r\nHost: tracker.test\r\n\r\n")]
    [InlineData("CONNECT :443 HTTP/1.1\r\nHost: :443\r\n\r\n")]
    [InlineData("CONNECT tracker.test:not-a-port HTTP/1.1\r\nHost: tracker.test\r\n\r\n")]
    [InlineData("CONNECT user@tracker.test:443 HTTP/1.1\r\nHost: tracker.test\r\n\r\n")]
    public async Task Proxy_RejectsMalformedConnectAuthority(string request)
    {
        var response = await SendRawRequestAsync(request);

        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_RejectsConnectWhenHeadersEndBeforeTerminator()
    {
        var response = await SendRawRequestAsync(
            "CONNECT tracker.test:443 HTTP/1.1\r\nHost: tracker.test:443\r\n",
            endRequestStream: true);

        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        Assert.Contains("Incomplete request headers", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_CancelsAndAwaitsIncompleteClient()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var proxyPort = ReservePort();
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings { ListenPort = proxyPort });
        await proxy.StartAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await client.GetStream().WriteAsync(
            Encoding.ASCII.GetBytes("GET http://tracker.test/announce?info_hash=abc"),
            timeout.Token);

        await proxy.StopAsync().WaitAsync(timeout.Token);

        Assert.False(proxy.IsRunning);
    }

    private static async Task<string> SendRawRequestAsync(string request, bool endRequestStream = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var proxyPort = ReservePort();
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings { ListenPort = proxyPort });
        await proxy.StartAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        if (endRequestStream)
            client.Client.Shutdown(SocketShutdown.Send);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, timeout.Token);
        return Encoding.ASCII.GetString(response.ToArray());
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class CapturingDebugLogger : IProxyDebugLogger
    {
        public List<string> Messages { get; } = [];

        public void Write(string message) => Messages.Add(message);
    }

    private static async Task RunTrackerAsync(
        TcpListener tracker,
        TaskCompletionSource<string> observed,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = await tracker.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var buffer = new byte[4096];
            var count = await stream.ReadAsync(buffer, cancellationToken);
            observed.TrySetResult(Encoding.Latin1.GetString(buffer, 0, count));
            var body = Encoding.ASCII.GetBytes("d8:completei1e10:incompletei6e8:intervali1800ee");
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
        finally
        {
            tracker.Stop();
        }
    }
}
