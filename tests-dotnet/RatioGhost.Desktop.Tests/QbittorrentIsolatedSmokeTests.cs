using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;
using RatioGhost.Proxy;

namespace RatioGhost.Desktop.Tests;

public sealed class QbittorrentIsolatedSmokeTests
{
    [Fact]
    public async Task InstalledQbittorrent_RoutesSyntheticAnnounceThroughRatioGhost()
    {
        if (!OperatingSystem.IsWindows())
            return;
        const string qbittorrent = @"C:\Program Files\qBittorrent\qbittorrent.exe";
        if (!File.Exists(qbittorrent))
            return;

        Exception? lastTransientFailure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await RunAttemptAsync(qbittorrent);
                return;
            }
            catch (OperationCanceledException exception) when (attempt < 2)
            {
                lastTransientFailure = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        ExceptionDispatchInfo.Capture(lastTransientFailure!).Throw();
    }

    private static async Task RunAttemptAsync(string qbittorrent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var userConfiguration = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "qBittorrent",
            "qBittorrent.ini");
        var userHashBefore = File.Exists(userConfiguration) ? HashFile(userConfiguration) : null;
        const string temporaryContainer = "RatioGhost.QbittorrentSmoke";
        var root = DisposableTestDirectory.Create(temporaryContainer);
        var profile = Path.Combine(root, "profile");
        var configurationDirectory = Path.Combine(
            profile,
            "qBittorrent_ratioghosttest",
            "config");
        Directory.CreateDirectory(configurationDirectory);
        var proxyPort = ReservePort();
        var tracker = new TcpListener(IPAddress.Loopback, 0);
        tracker.Start();
        var trackerPort = ((IPEndPoint)tracker.LocalEndpoint).Port;
        var observedRequest = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var trackerTask = RunTrackerAsync(tracker, observedRequest, timeout.Token);

        await File.WriteAllTextAsync(
            Path.Combine(configurationDirectory, "qBittorrent.ini"),
            BuildQbittorrentConfiguration(proxyPort),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            timeout.Token);
        var torrentPath = Path.Combine(root, "ratioghost-smoke.torrent");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(
            torrentPath,
            BuildTorrent($"http://127.0.0.1:{trackerPort}/announce"),
            timeout.Token);

        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings
            {
                ListenPort = proxyPort,
                PretendToSeed = true,
                OnlyTrackerTraffic = true
            });

        Process? process = null;
        try
        {
            await proxy.StartAsync(timeout.Token);
            process = Process.Start(new ProcessStartInfo
            {
                FileName = qbittorrent,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Minimized,
                ArgumentList =
                {
                    $"--profile={profile}",
                    "--configuration=ratioghosttest",
                    "--confirm-legal-notice",
                    "--no-splash",
                    "--skip-dialog=true",
                    "--add-stopped=false",
                    $"--save-path={root}",
                    torrentPath
                }
            }) ?? throw new InvalidOperationException("Could not start isolated qBittorrent.");

            var request = await observedRequest.Task.WaitAsync(timeout.Token);
            Assert.Contains("GET /announce?", request, StringComparison.Ordinal);
            Assert.Contains("info_hash=", request, StringComparison.Ordinal);
            Assert.Contains("left=0", request, StringComparison.Ordinal);
            Assert.Contains("uploaded=0", request, StringComparison.Ordinal);
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(10_000))
                            process.Kill(entireProcessTree: true);
                    }
                    process.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            try
            {
                await proxy.StopAsync();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            await timeout.CancelAsync();
            tracker.Stop();
            try
            {
                await trackerTask;
            }
            catch (Exception exception) when (exception is
                                              OperationCanceledException or
                                              IOException or
                                              SocketException)
            {
            }

            try
            {
                DisposableTestDirectory.Delete(root, temporaryContainer);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            if (cleanupFailures.Count > 0)
                throw new AggregateException("HTTP smoke-test cleanup failed.", cleanupFailures);
        }

        var userHashAfter = File.Exists(userConfiguration) ? HashFile(userConfiguration) : null;
        Assert.Equal(userHashBefore, userHashAfter);
    }

    private static string BuildQbittorrentConfiguration(int proxyPort) =>
        $"""
         [Meta]
         MigrationVersion=8

         [LegalNotice]
         Accepted=true

         [Network]
         Proxy\IP=127.0.0.1
         Proxy\Port={proxyPort}
         Proxy\Type=HTTP
         Proxy\HostnameLookupEnabled=true
         Proxy\Profiles\BitTorrent=true
         Proxy\Profiles\Misc=false
         Proxy\Profiles\RSS=false
         Proxy\AuthEnabled=false

         [BitTorrent]
         Session\ProxyPeerConnections=false
         Session\SSRFMitigation=false
         Session\DHTEnabled=false
         Session\PeXEnabled=false
         Session\LSDEnabled=false
         Session\StartPaused=false
         Session\QueueingSystemEnabled=false

         [Preferences]
         General\CloseToTrayNotified=true
         """;

    private static byte[] BuildTorrent(string announceUrl)
    {
        var prefix = Encoding.ASCII.GetBytes(
            $"d8:announce{Encoding.UTF8.GetByteCount(announceUrl)}:{announceUrl}" +
            "4:infod6:lengthi1e4:name20:ratioghost-smoke.bin12:piece lengthi16384e6:pieces20:");
        var suffix = Encoding.ASCII.GetBytes("ee");
        var result = new byte[prefix.Length + 20 + suffix.Length];
        prefix.CopyTo(result, 0);
        suffix.CopyTo(result, prefix.Length + 20);
        return result;
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
            var request = await ReadHeadersAsync(stream, cancellationToken);
            observed.TrySetResult(request);
            var body = Encoding.ASCII.GetBytes(
                "d8:completei0e10:incompletei1e8:intervali60ee");
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"),
                cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
        finally
        {
            tracker.Stop();
        }
    }

    private static async Task<string> ReadHeadersAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var current = new byte[1];
        while (output.Length < 64 * 1024)
        {
            await stream.ReadExactlyAsync(current, cancellationToken);
            output.WriteByte(current[0]);
            var buffer = output.GetBuffer();
            var length = (int)output.Length;
            if (length >= 4 &&
                buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                return Encoding.Latin1.GetString(output.ToArray());
        }
        throw new IOException("Tracker request headers exceeded 64 KiB.");
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
