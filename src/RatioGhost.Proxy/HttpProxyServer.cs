using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Collections.Concurrent;
using RatioGhost.Core.Announcements;
using RatioGhost.Core.Configuration;
using RatioGhost.Core.Platform;

namespace RatioGhost.Proxy;

public sealed class HttpProxyServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int MaximumOutboundAttempts = 2;
    private static readonly TimeSpan OutboundRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly AnnounceTransformer _transformer;
    private readonly Func<RatioGhostSettings> _settings;
    private readonly ICertificateAuthorityService? _certificates;
    private readonly Func<bool> _isPaused;
    private readonly IProxyDebugLogger? _debugLogger;
    private readonly Func<bool> _isDebugLogging;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _connectionLimit = new(32, 32);
    private readonly ConcurrentDictionary<long, Task> _activeConnections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptLoop;
    private long _connectionSequence;

    public HttpProxyServer(
        AnnounceTransformer transformer,
        Func<RatioGhostSettings> settings,
        ICertificateAuthorityService? certificates = null,
        HttpMessageHandler? outboundHandler = null,
        Func<bool>? isPaused = null,
        IProxyDebugLogger? debugLogger = null,
        Func<bool>? isDebugLogging = null)
    {
        _transformer = transformer;
        _settings = settings;
        _certificates = certificates;
        _isPaused = isPaused ?? (() => false);
        _debugLogger = debugLogger;
        _isDebugLogging = isDebugLogging ?? (() => false);
        _httpClient = new HttpClient(outboundHandler ?? new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    public event EventHandler<ProxyEvent>? Activity;

    public bool IsRunning => _listener is not null;

    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
            throw new InvalidOperationException("The proxy is already running.");

        var settings = _settings().Validate();
        var address = settings.OnlyLocalConnections ? IPAddress.Loopback : IPAddress.Any;
        _listener = new TcpListener(address, settings.ListenPort);
        _listener.Start(32);
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_listener, _shutdown.Token);
        PublishActivity(new ProxyEvent(DateTimeOffset.Now, AnnounceDisposition.Forwarded,
            $"Proxy listening on {address}:{BoundPort}."));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        var shutdown = Interlocked.Exchange(ref _shutdown, null);
        if (listener is null)
            return;

        await shutdown!.CancelAsync();
        listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException) when (shutdown.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
            {
            }
        }
        var activeConnections = _activeConnections.Values.ToArray();
        if (activeConnections.Length > 0)
            await Task.WhenAll(activeConnections);
        shutdown.Dispose();
        _acceptLoop = null;
    }

    private async Task AcceptLoopAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken);
            TrackConnection(client, cancellationToken);
        }
    }

    private void TrackConnection(TcpClient client, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _connectionSequence);
        var task = HandleBoundedAsync(client, cancellationToken);
        if (!_activeConnections.TryAdd(id, task))
            throw new InvalidOperationException("Could not track an accepted proxy connection.");
        _ = RemoveCompletedConnectionAsync(id, task);
    }

    private async Task RemoveCompletedConnectionAsync(long id, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            PublishActivity(new ProxyEvent(
                DateTimeOffset.Now,
                AnnounceDisposition.RejectedInvalid,
                $"Unexpected connection failure: {DescribeException(exception)}"));
        }
        finally
        {
            _activeConnections.TryRemove(id, out _);
        }
    }

    private async Task HandleBoundedAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await _connectionLimit.WaitAsync(cancellationToken);
            acquired = true;
            await HandleClientAsync(client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or HttpRequestException or
                                          TaskCanceledException or AuthenticationException or CryptographicException or
                                          InvalidOperationException or ArgumentException)
        {
            PublishActivity(new ProxyEvent(DateTimeOffset.Now, AnnounceDisposition.RejectedInvalid,
                $"Connection failed: {DescribeException(exception)}"));
        }
        finally
        {
            client.Dispose();
            if (acquired)
                _connectionLimit.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var clientStream = client.GetStream();
        var headerBytes = await ReadHeadersAsync(clientStream, cancellationToken);
        if (headerBytes is null)
        {
            await WriteErrorAsync(clientStream, 431, "Request Header Fields Too Large", cancellationToken);
            return;
        }
        if (!HasHeaderTerminator(headerBytes))
        {
            await WriteErrorAsync(clientStream, 400, "Incomplete request headers", cancellationToken);
            return;
        }

        var headerText = Encoding.Latin1.GetString(headerBytes);
        var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.None);
        var firstLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (firstLine.Length != 3)
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", cancellationToken);
            return;
        }

        if (firstLine[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConnectAsync(client, clientStream, firstLine[1], cancellationToken);
            return;
        }

        if (!firstLine[0].Equals("GET", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(firstLine[1], UriKind.Absolute, out var target))
        {
            await WriteErrorAsync(clientStream, 400, "Only absolute-form HTTP GET is supported", cancellationToken);
            return;
        }

        var effectiveSettings = GetEffectiveSettings(client);
        await ForwardHttpRequestAsync(clientStream, lines, target, effectiveSettings, cancellationToken);
    }

    private async Task HandleConnectAsync(
        TcpClient client,
        Stream clientStream,
        string authority,
        CancellationToken cancellationToken)
    {
        if (!TryParseAuthority(authority, out var host, out var port))
        {
            await WriteErrorAsync(clientStream, 400, "Invalid CONNECT authority", cancellationToken);
            return;
        }

        var settings = GetEffectiveSettings(client);
        if (port != 443)
        {
            if (settings.OnlyTrackerTraffic)
            {
                await WriteErrorAsync(clientStream, 403, "Opaque CONNECT blocked in tracker-only mode", cancellationToken);
                return;
            }
            await TunnelAsync(clientStream, host, port, cancellationToken);
            return;
        }

        if (_certificates is null || !_certificates.Capability.IsSupported)
        {
            await WriteErrorAsync(clientStream, 501, "HTTPS interception is unavailable on this platform", cancellationToken);
            return;
        }
        if (!await _certificates.IsTrustedAsync(cancellationToken))
        {
            await WriteErrorAsync(clientStream, 503,
                "RatioGhost local CA is not trusted; enable HTTPS explicitly in the application", cancellationToken);
            return;
        }

        using var serverCertificate = await _certificates.GetServerCertificateAsync(host, cancellationToken);
        await clientStream.WriteAsync(
            "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),
            cancellationToken);
        await clientStream.FlushAsync(cancellationToken);

        string? tlsServerName = null;
        using var tlsStream = new SslStream(clientStream, leaveInnerStreamOpen: true);
        await tlsStream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, serverName) =>
                {
                    tlsServerName = string.IsNullOrWhiteSpace(serverName) ? null : serverName;
                    return serverCertificate;
                },
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            cancellationToken);

        var decryptedHeaders = await ReadHeadersAsync(tlsStream, cancellationToken);
        if (decryptedHeaders is null)
            return;
        if (!HasHeaderTerminator(decryptedHeaders))
        {
            await WriteErrorAsync(tlsStream, 400, "Incomplete request headers", cancellationToken);
            return;
        }
        var lines = Encoding.Latin1.GetString(decryptedHeaders)
            .Split(["\r\n", "\n"], StringSplitOptions.None);
        var firstLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (firstLine.Length != 3 ||
            !firstLine[0].Equals("GET", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(firstLine[1], UriKind.RelativeOrAbsolute, out var requestTarget))
        {
            await WriteErrorAsync(tlsStream, 400, "Only HTTPS GET tracker requests are supported", cancellationToken);
            return;
        }

        if (!TryGetHostHeader(lines, port, out var headerHost))
        {
            await WriteErrorAsync(tlsStream, 400, "Invalid HTTPS Host header", cancellationToken);
            return;
        }

        var requestHost = SelectLogicalHost(host, headerHost, tlsServerName);
        Uri target;
        if (requestTarget.IsAbsoluteUri)
        {
            if (!requestTarget.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                (!requestTarget.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                 !requestTarget.Host.Equals(requestHost, StringComparison.OrdinalIgnoreCase)) ||
                requestTarget.Port != port)
            {
                await WriteErrorAsync(
                    tlsStream,
                    400,
                    "HTTPS request target must match the CONNECT authority",
                    cancellationToken);
                return;
            }
            target = BuildHttpsTarget(requestHost, port, requestTarget.PathAndQuery);
        }
        else
        {
            var resource = firstLine[1].StartsWith('/') ? firstLine[1] : "/" + firstLine[1];
            target = BuildHttpsTarget(requestHost, port, resource);
        }
        await ForwardHttpRequestAsync(tlsStream, lines, target, settings, cancellationToken);
    }

    private async Task ForwardHttpRequestAsync(
        Stream clientStream,
        string[] lines,
        Uri target,
        RatioGhostSettings effectiveSettings,
        CancellationToken cancellationToken)
    {
        var result = _transformer.Transform(target, effectiveSettings, paused: _isPaused());
        PublishActivity(new ProxyEvent(
            DateTimeOffset.Now, result.Disposition, result.Message, result.Target ?? target, result.InfoHash));
        if (result.Target is null)
        {
            await WriteErrorAsync(clientStream, 403, "Forbidden", cancellationToken);
            return;
        }

        HttpResponseMessage response;
        try
        {
            response = await SendTrackerRequestAsync(
                result.Target,
                lines.Skip(1),
                cancellationToken);
        }
        catch (Exception exception) when (IsOutboundFailure(exception) &&
                                          !cancellationToken.IsCancellationRequested)
        {
            PublishActivity(new ProxyEvent(
                DateTimeOffset.Now,
                AnnounceDisposition.RejectedInvalid,
                $"Tracker connection failed for {FormatEndpoint(result.Target)}: {DescribeException(exception)}",
                result.Target,
                result.InfoHash));
            await WriteErrorAsync(clientStream, 502, "Tracker connection failed", cancellationToken);
            return;
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (Exception exception) when (IsOutboundFailure(exception) &&
                                              !cancellationToken.IsCancellationRequested)
            {
                PublishActivity(new ProxyEvent(
                    DateTimeOffset.Now,
                    AnnounceDisposition.RejectedInvalid,
                    $"Tracker response failed for {FormatEndpoint(result.Target)}: {DescribeException(exception)}",
                    result.Target,
                    result.InfoHash));
                await WriteErrorAsync(clientStream, 502, "Tracker response failed", cancellationToken);
                return;
            }

            if (result.InfoHash is not null)
                _transformer.ObserveTrackerResponse(result.InfoHash, TrackerResponseParser.Parse(body));
            await WriteResponseAsync(clientStream, response, body, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendTrackerRequestAsync(
        Uri target,
        IEnumerable<string> headerLines,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumOutboundAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            CopyRequestHeaders(headerLines, request);
            try
            {
                return await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (Exception exception) when (IsOutboundFailure(exception) &&
                                              attempt < MaximumOutboundAttempts &&
                                              !cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                await Task.Delay(OutboundRetryDelay, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("The tracker request ended without a response.");
    }

    private static bool IsOutboundFailure(Exception exception) =>
        exception is HttpRequestException or SocketException or IOException or
        TaskCanceledException or AuthenticationException or CryptographicException;

    private static Uri BuildHttpsTarget(string host, int port, string resource)
    {
        var baseUri = new UriBuilder(Uri.UriSchemeHttps, host, port).Uri;
        return new Uri(baseUri, resource.StartsWith('/') ? resource : "/" + resource);
    }

    private static string SelectLogicalHost(
        string authorityHost,
        string? headerHost,
        string? tlsServerName)
    {
        if (!IPAddress.TryParse(authorityHost, out _))
            return authorityHost;

        if (IsDnsHost(headerHost))
            return headerHost!;
        if (IsDnsHost(tlsServerName))
            return tlsServerName!;
        return authorityHost;
    }

    private static bool IsDnsHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host) == UriHostNameType.Dns;

    private static bool TryGetHostHeader(
        IEnumerable<string> lines,
        int expectedPort,
        out string? host)
    {
        host = null;
        string? value = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value is not null)
                return false;
            value = line[(colon + 1)..].Trim();
        }

        return value is null || TryParseHostHeader(value, expectedPort, out host);
    }

    private static bool TryParseHostHeader(
        string value,
        int expectedPort,
        out string host)
    {
        host = string.Empty;
        if (value.Length == 0 || value.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate($"https://{value}", UriKind.Absolute, out var parsed) ||
            parsed.UserInfo.Length > 0 ||
            parsed.AbsolutePath != "/" ||
            parsed.Query.Length > 0 ||
            parsed.Fragment.Length > 0 ||
            parsed.Port != expectedPort ||
            Uri.CheckHostName(parsed.Host) == UriHostNameType.Unknown)
        {
            return false;
        }

        host = parsed.Host;
        return true;
    }

    private static string FormatEndpoint(Uri target)
    {
        var host = target.Host.Contains(':')
            ? $"[{target.Host}]"
            : target.Host;
        return target.IsDefaultPort ? host : $"{host}:{target.Port}";
    }

    private static string DescribeException(Exception exception)
    {
        var messages = new List<string>();
        var current = exception;
        for (var depth = 0; current is not null && depth < 4; depth++, current = current.InnerException)
        {
            var message = current.Message.Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length == 0)
                continue;
            messages.Add($"{current.GetType().Name}: {message}");
        }

        return messages.Count == 0
            ? exception.GetType().Name
            : string.Join(" -> ", messages);
    }

    private RatioGhostSettings GetEffectiveSettings(TcpClient client)
    {
        var effectiveSettings = _settings();
        if (client.Client.RemoteEndPoint is IPEndPoint remote && !IPAddress.IsLoopback(remote.Address))
            effectiveSettings = effectiveSettings with { OnlyTrackerTraffic = true };
        return effectiveSettings;
    }

    private static bool TryParseAuthority(string authority, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        string portText;
        if (authority.Length > 0 && authority[0] == '[')
        {
            var closingBracket = authority.IndexOf(']');
            if (closingBracket <= 1 ||
                closingBracket + 1 >= authority.Length ||
                authority[closingBracket + 1] != ':' ||
                !IPAddress.TryParse(authority[1..closingBracket], out var address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
            host = authority[1..closingBracket];
            portText = authority[(closingBracket + 2)..];
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon <= 0 || authority.AsSpan(0, colon).Contains(':'))
                return false;
            host = authority[..colon];
            portText = authority[(colon + 1)..];
            if (host.Any(character =>
                    char.IsWhiteSpace(character) || character is '/' or '\\' or '@' or '?' or '#') ||
                Uri.CheckHostName(host) == UriHostNameType.Unknown)
                return false;
        }

        if (portText.Length == 0 ||
            portText.Any(character => character is < '0' or > '9') ||
            !int.TryParse(portText, out port) ||
            port is < 1 or > 65535)
        {
            host = string.Empty;
            port = 0;
            return false;
        }
        return true;
    }

    private static bool HasHeaderTerminator(ReadOnlySpan<byte> headers) =>
        headers.EndsWith("\r\n\r\n"u8) || headers.EndsWith("\n\n"u8);

    private static async Task TunnelAsync(
        Stream clientStream,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using var remote = new TcpClient();
        await remote.ConnectAsync(host, port, cancellationToken);
        await clientStream.WriteAsync(
            "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),
            cancellationToken);
        await clientStream.FlushAsync(cancellationToken);
        await using var remoteStream = remote.GetStream();
        var outbound = clientStream.CopyToAsync(remoteStream, cancellationToken);
        var inbound = remoteStream.CopyToAsync(clientStream, cancellationToken);
        await Task.WhenAny(outbound, inbound);
    }

    private static void CopyRequestHeaders(IEnumerable<string> lines, HttpRequestMessage request)
    {
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Headers.TryAddWithoutValidation(name, line[(colon + 1)..].Trim());
        }
        request.Headers.ConnectionClose = true;
    }

    private static async Task<byte[]?> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var current = new byte[1];
        var state = 0;
        while (buffer.Length <= MaximumHeaderBytes)
        {
            var read = await stream.ReadAsync(current, cancellationToken);
            if (read == 0)
                return buffer.Length == 0 ? [] : buffer.ToArray();
            buffer.WriteByte(current[0]);
            state = (state, current[0]) switch
            {
                (0, 13) => 1,
                (1, 10) => 2,
                (2, 13) => 3,
                (3, 10) => 4,
                (_, 10) => state == 2 ? 4 : 0,
                _ => 0
            };
            if (state == 4)
                return buffer.ToArray();
        }
        return null;
    }

    private static async Task WriteResponseAsync(
        Stream destination,
        HttpResponseMessage response,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ')
            .Append(response.ReasonPhrase).Append("\r\n");
        AppendHeaders(builder, response.Headers);
        AppendHeaders(builder, response.Content.Headers,
            excluded: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Content-Length", "Transfer-Encoding", "Connection"
            });
        builder.Append("Content-Length: ").Append(body.Length).Append("\r\nConnection: close\r\n\r\n");
        await destination.WriteAsync(Encoding.Latin1.GetBytes(builder.ToString()), cancellationToken);
        await destination.WriteAsync(body, cancellationToken);
    }

    private static void AppendHeaders(
        StringBuilder builder,
        HttpHeaders headers,
        IReadOnlySet<string>? excluded = null)
    {
        foreach (var header in headers)
        {
            if (excluded?.Contains(header.Key) == true ||
                header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var value in header.Value)
                builder.Append(header.Key).Append(": ").Append(value).Append("\r\n");
        }
    }

    private void PublishActivity(ProxyEvent activity)
    {
        if (_debugLogger is not null)
        {
            try
            {
                if (_isDebugLogging())
                {
                    var target = activity.Target is null ? string.Empty : $" target={activity.Target}";
                    _debugLogger.Write(
                        ProxyDebugRedactor.RedactSensitive(
                            $"{activity.Disposition}: {activity.Message}{target}"));
                }
            }
            catch (Exception)
            {
                // A diagnostic sink must never interfere with proxy traffic.
            }
        }

        Activity?.Invoke(this, activity);
    }

    private static async Task WriteErrorAsync(
        Stream stream,
        int status,
        string reason,
        CancellationToken cancellationToken)
    {
        var safeReason = reason.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        var body = Encoding.UTF8.GetBytes(safeReason);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {safeReason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient.Dispose();
        _connectionLimit.Dispose();
    }
}
