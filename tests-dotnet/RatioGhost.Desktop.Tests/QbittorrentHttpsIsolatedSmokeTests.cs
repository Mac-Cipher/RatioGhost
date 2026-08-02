using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Runtime.ExceptionServices;
using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;
using RatioGhost.Core.Platform;
using RatioGhost.Proxy;

namespace RatioGhost.Desktop.Tests;

public sealed class QbittorrentHttpsIsolatedSmokeTests
{
    [Fact]
    public async Task InstalledQbittorrent_RoutesSyntheticHttpsAnnounceThroughMitm()
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
                await RunAttemptAsync(qbittorrent, validateTrackerCertificate: false);
                return;
            }
            catch (TaskCanceledException exception) when (attempt < 2)
            {
                lastTransientFailure = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        ExceptionDispatchInfo.Capture(lastTransientFailure!).Throw();
    }

    [Fact]
    public async Task InstalledQbittorrent_StrictTrackerValidationRejectsUntrustedMitm()
    {
        if (!OperatingSystem.IsWindows())
            return;
        const string qbittorrent = @"C:\Program Files\qBittorrent\qbittorrent.exe";
        if (!File.Exists(qbittorrent))
            return;

        await RunAttemptAsync(qbittorrent, validateTrackerCertificate: true);
    }

    private static async Task RunAttemptAsync(string qbittorrent, bool validateTrackerCertificate)
    {

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var userConfiguration = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "qBittorrent",
            "qBittorrent.ini");
        var userHashBefore = File.Exists(userConfiguration) ? HashFile(userConfiguration) : null;
        const string temporaryContainer = "RatioGhost.QbittorrentHttpsSmoke";
        var root = DisposableTestDirectory.Create(temporaryContainer);
        var profile = Path.Combine(root, "profile");
        var configurationDirectory = Path.Combine(
            profile,
            "qBittorrent_ratioghosthttpstest",
            "config");
        Directory.CreateDirectory(configurationDirectory);
        var proxyPort = ReservePort();

        using var trackerCertificates = new TrustedTrackerCertificates();
        var tracker = new TcpListener(IPAddress.Loopback, 443);
        try
        {
            tracker.Start();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(
                "The isolated qBittorrent HTTPS smoke test requires 127.0.0.1:443.",
                exception);
        }

        var observedRequest = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var trackerTask = RunTlsTrackerAsync(
            tracker,
            trackerCertificates.Server,
            observedRequest,
            timeout.Token);

        await File.WriteAllTextAsync(
            Path.Combine(configurationDirectory, "qBittorrent.ini"),
            BuildQbittorrentConfiguration(proxyPort, validateTrackerCertificate),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            timeout.Token);
        var torrentPath = Path.Combine(root, "ratioghost-https-smoke.torrent");
        await File.WriteAllBytesAsync(
            torrentPath,
            BuildTorrent("https://127.0.0.1/announce"),
            timeout.Token);

        using var certificateAuthority = new EphemeralCertificateAuthority();
        using var outboundHandler = CreateStrictTestRootHandler(trackerCertificates.Root);
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings
            {
                ListenPort = proxyPort,
                PretendToSeed = true,
                OnlyTrackerTraffic = true
            },
            certificateAuthority,
            outboundHandler);
        Process? process = null;
        try
        {
            await proxy.StartAsync(timeout.Token);

            var processStart = new ProcessStartInfo
            {
                FileName = qbittorrent,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Minimized,
                ArgumentList =
                {
                    $"--profile={profile}",
                    "--configuration=ratioghosthttpstest",
                    "--confirm-legal-notice",
                    "--no-splash",
                    "--skip-dialog=true",
                    "--add-stopped=false",
                    $"--save-path={root}",
                    torrentPath
                }
            };
            process = Process.Start(processStart) ??
                      throw new InvalidOperationException("Could not start isolated qBittorrent.");

            if (validateTrackerCertificate)
            {
                await Task.Delay(TimeSpan.FromSeconds(8), timeout.Token);
                Assert.False(
                    observedRequest.Task.IsCompleted,
                    "qBittorrent reached the tracker despite strict certificate validation and an untrusted MITM CA.");
            }
            else
            {
                var request = await observedRequest.Task.WaitAsync(timeout.Token);
                Assert.Contains("GET /announce?", request, StringComparison.Ordinal);
                Assert.Contains("info_hash=", request, StringComparison.Ordinal);
                Assert.Contains("left=0", request, StringComparison.Ordinal);
                Assert.Contains("uploaded=0", request, StringComparison.Ordinal);
            }
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
                                              SocketException or
                                              AuthenticationException)
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
                throw new AggregateException("HTTPS smoke-test cleanup failed.", cleanupFailures);
        }

        var userHashAfter = File.Exists(userConfiguration) ? HashFile(userConfiguration) : null;
        Assert.Equal(userHashBefore, userHashAfter);
    }

    private static SocketsHttpHandler CreateStrictTestRootHandler(X509Certificate2 root)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        handler.SslOptions.RemoteCertificateValidationCallback =
            (_, certificate, _, errors) =>
                certificate is not null &&
                (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                ValidateWithRoot(certificate, root);
        return handler;
    }

    private static bool ValidateWithRoot(X509Certificate certificate, X509Certificate2 root)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(
            certificate.Export(X509ContentType.Cert));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    private static string BuildQbittorrentConfiguration(int proxyPort, bool validateTrackerCertificate) =>
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
         Session\ValidateHTTPSTrackerCertificate={validateTrackerCertificate.ToString().ToLowerInvariant()}

         [Preferences]
         General\CloseToTrayNotified=true
         """;

    private static byte[] BuildTorrent(string announceUrl)
    {
        var prefix = Encoding.ASCII.GetBytes(
            $"d8:announce{Encoding.UTF8.GetByteCount(announceUrl)}:{announceUrl}" +
            "4:infod6:lengthi1e4:name26:ratioghost-https-smoke.bin12:piece lengthi16384e6:pieces20:");
        var suffix = Encoding.ASCII.GetBytes("ee");
        var result = new byte[prefix.Length + 20 + suffix.Length];
        prefix.CopyTo(result, 0);
        suffix.CopyTo(result, prefix.Length + 20);
        return result;
    }

    private static async Task RunTlsTrackerAsync(
        TcpListener tracker,
        X509Certificate2 serverCertificate,
        TaskCompletionSource<string> observed,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = await tracker.AcceptTcpClientAsync(cancellationToken);
            await using var network = client.GetStream();
            using var tls = new SslStream(network, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                cancellationToken);
            var request = await ReadHeadersAsync(tls, cancellationToken);
            observed.TrySetResult(request);
            var body = Encoding.ASCII.GetBytes(
                "d8:completei0e10:incompletei1e8:intervali60ee");
            await tls.WriteAsync(
                Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"),
                cancellationToken);
            await tls.WriteAsync(body, cancellationToken);
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

    private sealed class TrustedTrackerCertificates : IDisposable
    {
        private readonly X509Certificate2 _root;

        public TrustedTrackerCertificates()
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest(
                $"CN=RatioGhost isolated tracker root {Guid.NewGuid():N}",
                rootKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, true, 0, true));
            rootRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                    true));
            _root = rootRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(1));

            using var serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest(
                "CN=127.0.0.1",
                serverKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            serverRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    true));
            serverRequest.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                    true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            serverRequest.CertificateExtensions.Add(san.Build());
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;
            using var signed = serverRequest.Create(
                _root,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(1),
                serial);
            using var withKey = signed.CopyWithPrivateKey(serverKey);
            Server = X509CertificateLoader.LoadPkcs12(
                withKey.Export(X509ContentType.Pfx),
                null,
                X509KeyStorageFlags.Exportable);
        }

        public X509Certificate2 Root => _root;
        public X509Certificate2 Server { get; }

        public void Dispose()
        {
            Server.Dispose();
            _root.Dispose();
        }
    }

    private sealed class EphemeralCertificateAuthority : ICertificateAuthorityService, IDisposable
    {
        private readonly RSA _rootKey = RSA.Create(2048);

        public EphemeralCertificateAuthority()
        {
            var request = new CertificateRequest(
                $"CN=RatioGhost qBittorrent HTTPS test {Guid.NewGuid():N}",
                _rootKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                    true));
            Root = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(1));
        }

        public X509Certificate2 Root { get; }
        public PlatformCapability Capability { get; } = new(true, "Ephemeral test CA");

        public Task<bool> IsTrustedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task RequestTrustAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveTrustAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<X509Certificate2> GetServerCertificateAsync(
            string host,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={host}",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                    true));
            var san = new SubjectAlternativeNameBuilder();
            if (IPAddress.TryParse(host, out var address))
                san.AddIpAddress(address);
            else
                san.AddDnsName(host);
            request.CertificateExtensions.Add(san.Build());
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;
            using var signed = request.Create(
                Root,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(1),
                serial);
            using var withKey = signed.CopyWithPrivateKey(key);
            return Task.FromResult(X509CertificateLoader.LoadPkcs12(
                withKey.Export(X509ContentType.Pfx),
                null,
                X509KeyStorageFlags.Exportable));
        }

        public void Dispose()
        {
            Root.Dispose();
            _rootKey.Dispose();
        }
    }
}
