using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;
using RatioGhost.Core.Platform;

namespace RatioGhost.Proxy.Tests;

public sealed class HttpsMitmTests
{
    [Fact]
    public async Task Connect_PerformsTlsHandshakeAndRewritesDecryptedAnnounce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var certificates = new TestCertificateAuthority();
        using var outbound = new RecordingHandler();
        var proxyPort = ReservePort();
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings
            {
                ListenPort = proxyPort,
                ReportDownloadAsZero = true,
                PretendToSeed = true
            },
            certificates,
            outbound);
        var activities = new List<string>();
        proxy.Activity += (_, activity) => activities.Add(activity.Message);
        await proxy.StartAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await using var network = client.GetStream();
        await network.WriteAsync(
            Encoding.ASCII.GetBytes("CONNECT tracker.test:443 HTTP/1.1\r\nHost: tracker.test:443\r\n\r\n"),
            timeout.Token);
        var connectResponse = await ReadHeadersAsync(network, timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", connectResponse, StringComparison.Ordinal);

        using var tls = new SslStream(
            network,
            leaveInnerStreamOpen: true,
            (_, certificate, _, _) => ValidateWithTestRoot(certificate, certificates.Root));
        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "tracker.test",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                timeout.Token);
        }
        catch (IOException exception)
        {
            await Task.Delay(100, timeout.Token);
            throw new InvalidOperationException(string.Join(" | ", activities), exception);
        }
        await tls.WriteAsync(
            Encoding.ASCII.GetBytes(
                "GET /announce?info_hash=abc&downloaded=50&uploaded=20&left=700 HTTP/1.1\r\nHost: tracker.test\r\nConnection: close\r\n\r\n"),
            timeout.Token);
        using var response = new MemoryStream();
        await tls.CopyToAsync(response, timeout.Token);

        Assert.NotNull(outbound.ObservedTarget);
        Assert.Equal("https", outbound.ObservedTarget!.Scheme);
        Assert.Equal("tracker.test", outbound.ObservedTarget.Host);
        Assert.Contains("downloaded=0", outbound.ObservedTarget.Query, StringComparison.Ordinal);
        Assert.Contains("uploaded=20", outbound.ObservedTarget.Query, StringComparison.Ordinal);
        Assert.Contains("left=0", outbound.ObservedTarget.Query, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(response.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_FailsBeforeTlsWhenCaIsNotTrusted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var certificates = new TestCertificateAuthority(isTrusted: false);
        var proxyPort = ReservePort();
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings { ListenPort = proxyPort },
            certificates);
        await proxy.StartAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes("CONNECT tracker.test:443 HTTP/1.1\r\n\r\n"),
            timeout.Token);

        Assert.StartsWith(
            "HTTP/1.1 503",
            await ReadHeadersAsync(stream, timeout.Token),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_RejectsAbsoluteTargetOutsideTunnelAuthority()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var certificates = new TestCertificateAuthority();
        using var outbound = new RecordingHandler();
        var proxyPort = ReservePort();
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings { ListenPort = proxyPort },
            certificates,
            outbound);
        await proxy.StartAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await using var network = client.GetStream();
        await network.WriteAsync(
            Encoding.ASCII.GetBytes("CONNECT tracker.test:443 HTTP/1.1\r\nHost: tracker.test:443\r\n\r\n"),
            timeout.Token);
        Assert.StartsWith(
            "HTTP/1.1 200",
            await ReadHeadersAsync(network, timeout.Token),
            StringComparison.Ordinal);

        using var tls = new SslStream(
            network,
            leaveInnerStreamOpen: true,
            (_, certificate, _, _) => ValidateWithTestRoot(certificate, certificates.Root));
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "tracker.test",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            timeout.Token);
        await tls.WriteAsync(
            Encoding.ASCII.GetBytes(
                "GET https://other.test/announce?info_hash=abc&downloaded=1&uploaded=1&left=1 HTTP/1.1\r\n" +
                "Host: other.test\r\nConnection: close\r\n\r\n"),
            timeout.Token);

        var response = await ReadHeadersAsync(tls, timeout.Token);
        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        Assert.Contains("must match the CONNECT authority", response, StringComparison.Ordinal);
        Assert.Null(outbound.ObservedTarget);
    }

    [Fact]
    public async Task OutboundTls_RejectsAnUntrustedTrackerCertificate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var trackerCertificate = CreateSelfSignedServerCertificate("localhost");
        var tracker = new TcpListener(IPAddress.Loopback, 0);
        tracker.Start();
        var trackerPort = ((IPEndPoint)tracker.LocalEndpoint).Port;
        var trackerTask = RunUntrustedTlsTrackerAsync(tracker, trackerCertificate, timeout.Token);
        var proxyPort = ReservePort();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = new HttpProxyServer(
            new AnnounceTransformer(),
            () => new RatioGhostSettings { ListenPort = proxyPort });
        proxy.Activity += (_, activity) =>
        {
            if (activity.Disposition == AnnounceDisposition.RejectedInvalid)
                failure.TrySetResult(activity.Message);
        };
        await proxy.StartAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        await using var stream = client.GetStream();
        var target =
            $"https://127.0.0.1:{trackerPort}/announce?info_hash=abc&downloaded=1&uploaded=1&left=1";
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:{trackerPort}\r\n\r\n"),
            timeout.Token);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, timeout.Token);

        Assert.Empty(response.ToArray());
        Assert.Contains("Connection failed", await failure.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);
        await trackerTask;
    }

    private static bool ValidateWithTestRoot(X509Certificate? certificate, X509Certificate2 root)
    {
        if (certificate is null)
            return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var current = new byte[1];
        while (output.Length < 64 * 1024)
        {
            var count = await stream.ReadAsync(current, cancellationToken);
            if (count == 0)
                break;
            output.WriteByte(current[0]);
            var bytes = output.GetBuffer();
            var length = (int)output.Length;
            if (length >= 4 &&
                bytes[length - 4] == '\r' && bytes[length - 3] == '\n' &&
                bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                break;
        }
        return Encoding.Latin1.GetString(output.ToArray());
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateSelfSignedServerCertificate(string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={host}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.Exportable);
    }

    private static async Task RunUntrustedTlsTrackerAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    },
                    cancellationToken);
                var oneByte = new byte[1];
                await tls.ReadExactlyAsync(oneByte, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or AuthenticationException or EndOfStreamException)
            {
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? ObservedTarget { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObservedTarget = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    Encoding.ASCII.GetBytes("d8:completei1e10:incompletei6e8:intervali1800ee"))
            });
        }
    }

    private sealed class TestCertificateAuthority : ICertificateAuthorityService, IDisposable
    {
        private readonly RSA _rootKey = RSA.Create(2048);
        private readonly bool _isTrusted;

        public TestCertificateAuthority(bool isTrusted = true)
        {
            _isTrusted = isTrusted;
            var request = new CertificateRequest(
                "CN=RatioGhost Test Root",
                _rootKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            Root = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(1));
        }

        public X509Certificate2 Root { get; }

        public PlatformCapability Capability { get; } = new(true, "Test CA");

        public Task<bool> IsTrustedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_isTrusted);

        public Task RequestTrustAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveTrustAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<X509Certificate2> GetServerCertificateAsync(
            string host,
            CancellationToken cancellationToken = default)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={host}",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    true));
            var san = new SubjectAlternativeNameBuilder();
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
