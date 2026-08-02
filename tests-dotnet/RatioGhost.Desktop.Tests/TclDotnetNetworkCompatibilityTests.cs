using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;

namespace RatioGhost.Desktop.Tests;

public sealed class TclDotnetNetworkCompatibilityTests
{
    [Fact]
    public async Task TclProxyAndDotnet_MatchPeerThresholdAndCompletedFreeleechSequence()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var repository = FindRepositoryRoot();
        var legacyRuntime = Path.Combine(repository, "tclkit.exe");
        if (!File.Exists(legacyRuntime))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.TclNetworkOracle",
            Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "appdata", "RatioGhost");
        Directory.CreateDirectory(profile);

        var tracker = new TcpListener(IPAddress.Loopback, 0);
        tracker.Start();
        var trackerPort = ((IPEndPoint)tracker.LocalEndpoint).Port;
        var proxyPort = ReservePortPair();
        await File.WriteAllTextAsync(
            Path.Combine(profile, "settings.dat"),
            BuildLegacySettings(proxyPort),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            timeout.Token);
        var observed = new List<string>();
        var trackerTask = RunTrackerAsync(tracker, observed, timeout.Token, requestCount: 5);
        Process? legacy = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = legacyRuntime,
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { Path.Combine(repository, "rghost.vfs", "main.tcl"), "m" }
            };
            start.Environment["APPDATA"] = Path.Combine(root, "appdata");
            start.Environment["RATIOGHOST_ALLOW_MULTIPLE"] = "1";
            start.Environment["RATIOGHOST_ISOLATED_TEST"] = "1";
            legacy = Process.Start(start) ??
                     throw new InvalidOperationException("Could not start the isolated Tcl reference.");
            await WaitForListenerAsync(proxyPort, legacy, timeout.Token);

            var first =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=100&uploaded=200&left=500&event=started";
            var second =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=150&uploaded=260&left=450";
            var completed =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=200&uploaded=300&left=0&event=completed";
            var afterCompleted =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=210&uploaded=330&left=0";
            var final =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=220&uploaded=360&left=0";
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, first, timeout.Token),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, second, timeout.Token),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, completed, timeout.Token),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, afterCompleted, timeout.Token),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, final, timeout.Token),
                StringComparison.Ordinal);
            await trackerTask;

            Assert.Equal(5, observed.Count);
            var tclSecondTarget = observed[1].Split(' ', 3)[1];
            var tclCompletedTarget = observed[2].Split(' ', 3)[1];
            var tclAfterCompletedTarget = observed[3].Split(' ', 3)[1];
            var tclFinalTarget = observed[4].Split(' ', 3)[1];
            var dotnetTargets = RunDotnetSequence(trackerPort);
            Assert.Equal(dotnetTargets.Second, tclSecondTarget);
            Assert.Equal(dotnetTargets.Completed, tclCompletedTarget);
            Assert.Equal(dotnetTargets.AfterCompleted, tclAfterCompletedTarget);
            Assert.Equal(dotnetTargets.Final, tclFinalTarget);
            Assert.Contains("uploaded=540", tclSecondTarget, StringComparison.Ordinal);
            Assert.Contains("downloaded=0", tclSecondTarget, StringComparison.Ordinal);
            Assert.Contains("left=0", tclSecondTarget, StringComparison.Ordinal);
            Assert.Contains("uploaded=800", tclCompletedTarget, StringComparison.Ordinal);
            Assert.DoesNotContain("event=", tclCompletedTarget, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("uploaded=940", tclAfterCompletedTarget, StringComparison.Ordinal);
            Assert.Contains("uploaded=1080", tclFinalTarget, StringComparison.Ordinal);
        }
        finally
        {
            tracker.Stop();
            await timeout.CancelAsync();
            if (legacy is not null)
            {
                if (!legacy.HasExited)
                {
                    legacy.CloseMainWindow();
                    if (!legacy.WaitForExit(5_000))
                        legacy.Kill(entireProcessTree: true);
                }
                legacy.Dispose();
            }
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
        }
    }

    [Fact]
    public async Task TclProxyAndDotnet_MatchPausedCountersSequence()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var repository = FindRepositoryRoot();
        var legacyRuntime = Path.Combine(repository, "tclkit.exe");
        if (!File.Exists(legacyRuntime))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.TclPausedNetworkOracle",
            Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "appdata", "RatioGhost");
        Directory.CreateDirectory(profile);

        var tracker = new TcpListener(IPAddress.Loopback, 0);
        tracker.Start();
        var trackerPort = ((IPEndPoint)tracker.LocalEndpoint).Port;
        var proxyPort = ReservePortPair();
        await File.WriteAllTextAsync(
            Path.Combine(profile, "settings.dat"),
            BuildLegacySettings(proxyPort, noDownload: false, seed: false),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            timeout.Token);
        var observed = new List<string>();
        var trackerTask = RunTrackerAsync(tracker, observed, timeout.Token, requestCount: 2);
        Process? legacy = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = legacyRuntime,
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { Path.Combine(repository, "rghost.vfs", "main.tcl"), "m" }
            };
            start.Environment["APPDATA"] = Path.Combine(root, "appdata");
            start.Environment["RATIOGHOST_ALLOW_MULTIPLE"] = "1";
            start.Environment["RATIOGHOST_ISOLATED_TEST"] = "1";
            start.Environment["RATIOGHOST_ISOLATED_PAUSED"] = "1";
            legacy = Process.Start(start) ??
                     throw new InvalidOperationException("Could not start the isolated paused Tcl reference.");
            await WaitForListenerAsync(proxyPort, legacy, timeout.Token);

            var first =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-paused" +
                "&downloaded=100&uploaded=200&left=10&event=started";
            var second =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-paused" +
                "&downloaded=5&uploaded=7&left=10";
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, first, timeout.Token),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, second, timeout.Token),
                StringComparison.Ordinal);
            await trackerTask;

            Assert.Equal(2, observed.Count);
            var tclFirstTarget = observed[0].Split(' ', 3)[1];
            var tclSecondTarget = observed[1].Split(' ', 3)[1];
            var dotnetTargets = RunDotnetPausedSequence(trackerPort);
            Assert.Equal(dotnetTargets.First, tclFirstTarget);
            Assert.Equal(dotnetTargets.Second, tclSecondTarget);
            Assert.Contains("downloaded=100", tclFirstTarget, StringComparison.Ordinal);
            Assert.Contains("uploaded=200", tclFirstTarget, StringComparison.Ordinal);
            Assert.Contains("downloaded=100", tclSecondTarget, StringComparison.Ordinal);
            Assert.Contains("uploaded=200", tclSecondTarget, StringComparison.Ordinal);
        }
        finally
        {
            tracker.Stop();
            await timeout.CancelAsync();
            if (legacy is not null)
            {
                if (!legacy.HasExited)
                {
                    legacy.CloseMainWindow();
                    if (!legacy.WaitForExit(5_000))
                        legacy.Kill(entireProcessTree: true);
                }
                legacy.Dispose();
            }
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
        }
    }

    [Fact]
    public async Task TclProxyAndDotnet_MatchDeterministicRandomRatiosAndBoost()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var repository = FindRepositoryRoot();
        var legacyRuntime = Path.Combine(repository, "tclkit.exe");
        if (!File.Exists(legacyRuntime))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.TclRandomNetworkOracle",
            Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "appdata", "RatioGhost");
        Directory.CreateDirectory(profile);

        var tracker = new TcpListener(IPAddress.Loopback, 0);
        tracker.Start();
        var trackerPort = ((IPEndPoint)tracker.LocalEndpoint).Port;
        var proxyPort = ReservePortPair();
        await File.WriteAllTextAsync(
            Path.Combine(profile, "settings.dat"),
            BuildLegacySettings(
                proxyPort,
                noDownload: false,
                seed: false,
                upDownA: 2,
                upDownB: 1,
                upUpA: 4,
                upUpB: 2,
                boost: 1,
                boostChance: 100),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            timeout.Token);
        var observed = new List<string>();
        var trackerTask = RunTrackerAsync(tracker, observed, timeout.Token, requestCount: 2);
        Process? legacy = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = legacyRuntime,
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { Path.Combine(repository, "rghost.vfs", "main.tcl"), "m" }
            };
            start.Environment["APPDATA"] = Path.Combine(root, "appdata");
            start.Environment["RATIOGHOST_ALLOW_MULTIPLE"] = "1";
            start.Environment["RATIOGHOST_ISOLATED_TEST"] = "1";
            start.Environment["RATIOGHOST_ISOLATED_RANDOM_SEQUENCE"] = "0.25,0.75,0.10,0.50";
            start.Environment["RATIOGHOST_ISOLATED_ELAPSED_SECONDS"] = "7";
            legacy = Process.Start(start) ??
                     throw new InvalidOperationException("Could not start the isolated random Tcl reference.");
            await WaitForListenerAsync(proxyPort, legacy, timeout.Token);

            var first =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-random" +
                "&downloaded=100&uploaded=200&left=50&event=started";
            var second =
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-random" +
                "&downloaded=120&uploaded=230&left=40";
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, first, timeout.Token),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "HTTP/1.1 200",
                await SendProxyRequestAsync(proxyPort, second, timeout.Token),
                StringComparison.Ordinal);
            await trackerTask;

            Assert.Equal(2, observed.Count);
            var tclFirstTarget = observed[0].Split(' ', 3)[1];
            var tclSecondTarget = observed[1].Split(' ', 3)[1];
            var dotnetTargets = RunDotnetDeterministicRatioSequence(trackerPort);
            Assert.Equal(dotnetTargets.First, tclFirstTarget);
            Assert.Equal(dotnetTargets.Second, tclSecondTarget);
            Assert.Contains("uploaded=3944", tclSecondTarget, StringComparison.Ordinal);
        }
        finally
        {
            tracker.Stop();
            await timeout.CancelAsync();
            if (legacy is not null)
            {
                if (!legacy.HasExited)
                {
                    legacy.CloseMainWindow();
                    if (!legacy.WaitForExit(5_000))
                        legacy.Kill(entireProcessTree: true);
                }
                legacy.Dispose();
            }
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
        }
    }

    private static (string Second, string Completed, string AfterCompleted, string Final) RunDotnetSequence(int trackerPort)
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        var settings = CreateSettings();
        var first = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=100&uploaded=200&left=500&event=started"),
            settings);
        Assert.Equal(AnnounceDisposition.Rewritten, first.Disposition);
        transformer.ObserveTrackerResponse(
            "compat-network",
            new TrackerResponse(1, 5, 60, null));
        var second = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=150&uploaded=260&left=450"),
            settings);
        Assert.Equal(AnnounceDisposition.Rewritten, second.Disposition);
        transformer.ObserveTrackerResponse(
            "compat-network",
            new TrackerResponse(1, 5, 60, null));
        var completed = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=200&uploaded=300&left=0&event=completed"),
            settings);
        Assert.Equal(AnnounceDisposition.Rewritten, completed.Disposition);
        transformer.ObserveTrackerResponse(
            "compat-network",
            new TrackerResponse(1, 5, 60, null));
        var afterCompleted = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=210&uploaded=330&left=0"),
            settings);
        Assert.Equal(AnnounceDisposition.Rewritten, afterCompleted.Disposition);
        var final = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-network" +
                "&downloaded=220&uploaded=360&left=0"),
            settings);
        Assert.Equal(AnnounceDisposition.Rewritten, final.Disposition);
        return (
            second.Target!.PathAndQuery,
            completed.Target!.PathAndQuery,
            afterCompleted.Target!.PathAndQuery,
            final.Target!.PathAndQuery);
    }

    private static (string First, string Second) RunDotnetPausedSequence(int trackerPort)
    {
        var transformer = new AnnounceTransformer(new FixedRandomSource());
        var settings = new RatioGhostSettings();
        var first = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-paused" +
                "&downloaded=100&uploaded=200&left=10&event=started"),
            settings,
            paused: true,
            now: DateTimeOffset.UnixEpoch);
        Assert.Equal(AnnounceDisposition.Rewritten, first.Disposition);
        var second = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-paused" +
                "&downloaded=5&uploaded=7&left=10"),
            settings,
            paused: true,
            now: DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Equal(AnnounceDisposition.Rewritten, second.Disposition);
        return (first.Target!.PathAndQuery, second.Target!.PathAndQuery);
    }

    private static (string First, string Second) RunDotnetDeterministicRatioSequence(int trackerPort)
    {
        var transformer = new AnnounceTransformer(
            new SequenceRandomSource(0.25, 0.75, 0.10, 0.50));
        var settings = new RatioGhostSettings
        {
            UploadPerDownloadMinimum = 2,
            UploadPerDownloadMaximum = 1,
            UploadPerUploadMinimum = 4,
            UploadPerUploadMaximum = 2,
            BoostKiBPerSecond = 1,
            BoostChancePercent = 100
        };
        var first = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-random" +
                "&downloaded=100&uploaded=200&left=50&event=started"),
            settings,
            now: DateTimeOffset.UnixEpoch);
        Assert.Equal(AnnounceDisposition.Rewritten, first.Disposition);
        transformer.ObserveTrackerResponse(
            "compat-random",
            new TrackerResponse(1, 5, 60, null));
        var second = transformer.Transform(
            new Uri(
                $"http://127.0.0.1:{trackerPort}/announce?info_hash=compat-random" +
                "&downloaded=120&uploaded=230&left=40"),
            settings,
            now: DateTimeOffset.UnixEpoch.AddSeconds(7));
        Assert.Equal(AnnounceDisposition.Rewritten, second.Disposition);
        return (first.Target!.PathAndQuery, second.Target!.PathAndQuery);
    }

    private static RatioGhostSettings CreateSettings() => new()
    {
        MinimumPeers = 5,
        UploadPerDownloadMinimum = 2,
        UploadPerDownloadMaximum = 2,
        UploadPerUploadMinimum = 3,
        UploadPerUploadMaximum = 3,
        BoostChancePercent = 0,
        ReportDownloadAsZero = true,
        PretendToSeed = true
    };

    private static string BuildLegacySettings(
        int proxyPort,
        bool noDownload = true,
        bool seed = true,
        int upDownA = 2,
        int upDownB = 2,
        int upUpA = 3,
        int upUpB = 3,
        int boost = 0,
        int boostChance = 0) =>
        $"listen_port {proxyPort} listen_port_https {proxyPort + 1} " +
        "only_tracker 1 only_local 1 proxy_debug_logging 0 update 0 autostart 0 start_minimized 1 " +
        $"min_peers 5 updown_ratio_a {upDownA} updown_ratio_b {upDownB} " +
        $"upup_ratio_a {upUpA} upup_ratio_b {upUpB} boost {boost} " +
        $"boost_chance {boostChance} no_download {(noDownload ? 1 : 0)} seed {(seed ? 1 : 0)}";

    private static async Task RunTrackerAsync(
        TcpListener listener,
        List<string> observed,
        CancellationToken cancellationToken,
        int requestCount = 3)
    {
        try
        {
            for (var requestNumber = 0; requestNumber < requestCount; requestNumber++)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                var request = await ReadHeadersAsync(stream, cancellationToken);
                observed.Add(request.Split(["\r\n", "\n"], StringSplitOptions.None)[0]);
                var body = Encoding.ASCII.GetBytes(
                    "d8:completei1e10:incompletei5e8:intervali60ee");
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"),
                    cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> SendProxyRequestAsync(
        int proxyPort,
        string target,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        await using var stream = client.GetStream();
        var authority = new Uri(target).Authority;
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(
                $"GET {target} HTTP/1.1\r\nHost: {authority}\r\nConnection: close\r\n\r\n"),
            cancellationToken);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, cancellationToken);
        return Encoding.Latin1.GetString(response.ToArray());
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
            var bytes = output.GetBuffer();
            var length = (int)output.Length;
            if (length >= 4 &&
                bytes[length - 4] == '\r' && bytes[length - 3] == '\n' &&
                bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                return Encoding.Latin1.GetString(output.ToArray());
        }
        throw new IOException("Tracker request headers exceeded 64 KiB.");
    }

    private static async Task WaitForListenerAsync(
        int port,
        Process process,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"The isolated Tcl reference exited with code {process.ExitCode}.");
            using var probe = new TcpClient();
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static int ReservePortPair()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var first = new TcpListener(IPAddress.Loopback, 0);
            first.Start();
            var port = ((IPEndPoint)first.LocalEndpoint).Port;
            if (port >= 65535)
            {
                first.Stop();
                continue;
            }
            var second = new TcpListener(IPAddress.Loopback, port + 1);
            try
            {
                second.Start();
                return port;
            }
            catch (SocketException)
            {
            }
            finally
            {
                second.Stop();
                first.Stop();
            }
        }
        throw new InvalidOperationException("Could not reserve adjacent proxy ports.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tclkit.exe")) &&
                File.Exists(Path.Combine(directory.FullName, "rghost.vfs", "main.tcl")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the RatioGhost repository root.");
    }

    private sealed class FixedRandomSource : IRandomSource
    {
        public double NextDouble() => 0;
    }

    private sealed class SequenceRandomSource(params double[] values) : IRandomSource
    {
        private int _index;

        public double NextDouble() => _index < values.Length
            ? values[_index++]
            : throw new InvalidOperationException("The deterministic random sequence was exhausted.");
    }
}
