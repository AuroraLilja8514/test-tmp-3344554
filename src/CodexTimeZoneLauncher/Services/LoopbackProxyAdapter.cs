using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using CodexTimeZoneLauncher.Models;

namespace CodexTimeZoneLauncher.Services;

public sealed class LoopbackProxyAdapter : IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private readonly ProxyLaunchConfiguration _upstream;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public LoopbackProxyAdapter(ProxyLaunchConfiguration upstream, Action<string> log)
    {
        _upstream = upstream;
        _log = log;
    }

    public int Port { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_listener is not null) throw new InvalidOperationException("Proxy adapter is already running.");

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        _log($"Local proxy adapter: http://127.0.0.1:{Port} -> {_upstream.SafeDisplay}");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleClientSafelyAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var request = await ReadHeaderAsync(stream, cancellationToken);
                var parsed = ParseRequest(request.Header);

                if (parsed.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    var (host, port) = ParseAuthority(parsed.Target, 443);
                    await HandleConnectAsync(stream, request.Extra, host, port, cancellationToken);
                }
                else
                {
                    await HandlePlainHttpAsync(stream, request.Extra, parsed, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _log($"Proxy connection failed: {ex.Message}");
            }
        }
    }

    private async Task HandleConnectAsync(
        Stream client,
        byte[] clientExtra,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        await using var outbound = await OpenTunnelAsync(host, port, cancellationToken);
        await WriteAsciiAsync(client, "HTTP/1.1 200 Connection Established\r\nProxy-Agent: CodexTimeZoneLauncher\r\n\r\n", cancellationToken);

        if (outbound.InitialRead.Length > 0)
            await client.WriteAsync(outbound.InitialRead.AsMemory(), cancellationToken);
        if (clientExtra.Length > 0)
            await outbound.Stream.WriteAsync(clientExtra.AsMemory(), cancellationToken);

        await RelayBothWaysAsync(client, outbound.Stream, cancellationToken);
    }

    private async Task HandlePlainHttpAsync(
        Stream client,
        byte[] clientExtra,
        ParsedRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryResolveHttpTarget(request, out var host, out var port, out var originTarget, out var absoluteTarget))
        {
            await WriteAsciiAsync(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", cancellationToken);
            return;
        }

        if (_upstream.Kind is ProxyKind.Http or ProxyKind.Https)
        {
            await using var proxy = await OpenProxyTransportAsync(cancellationToken);
            var header = BuildForwardHeader(request, absoluteTarget, includeProxyAuthorization: true);
            await proxy.Stream.WriteAsync(header.AsMemory(), cancellationToken);
            if (clientExtra.Length > 0) await proxy.Stream.WriteAsync(clientExtra.AsMemory(), cancellationToken);
            await RelayBothWaysAsync(client, proxy.Stream, cancellationToken);
            return;
        }

        await using var outbound = await OpenTunnelAsync(host, port, cancellationToken);
        var directHeader = BuildForwardHeader(request, originTarget, includeProxyAuthorization: false);
        await outbound.Stream.WriteAsync(directHeader.AsMemory(), cancellationToken);
        if (clientExtra.Length > 0) await outbound.Stream.WriteAsync(clientExtra.AsMemory(), cancellationToken);
        if (outbound.InitialRead.Length > 0) await client.WriteAsync(outbound.InitialRead.AsMemory(), cancellationToken);
        await RelayBothWaysAsync(client, outbound.Stream, cancellationToken);
    }

    private async Task<OutboundConnection> OpenTunnelAsync(string host, int port, CancellationToken cancellationToken)
    {
        return _upstream.Kind switch
        {
            ProxyKind.Direct => await OpenDirectAsync(host, port, cancellationToken),
            ProxyKind.Http or ProxyKind.Https => await OpenHttpProxyTunnelAsync(host, port, cancellationToken),
            ProxyKind.Socks5 or ProxyKind.Socks5h => await OpenSocks5TunnelAsync(host, port, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported proxy type: {_upstream.Kind}"),
        };
    }

    private static async Task<OutboundConnection> OpenDirectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken);
            return new OutboundConnection(tcp, tcp.GetStream());
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task<OutboundConnection> OpenHttpProxyTunnelAsync(string host, int port, CancellationToken cancellationToken)
    {
        var proxy = await OpenProxyTransportAsync(cancellationToken);
        try
        {
            var authority = FormatAuthority(host, port);
            var builder = new StringBuilder()
                .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
                .Append("Host: ").Append(authority).Append("\r\n")
                .Append("Proxy-Connection: Keep-Alive\r\n");
            AppendProxyAuthorization(builder);
            builder.Append("\r\n");
            await WriteAsciiAsync(proxy.Stream, builder.ToString(), cancellationToken);

            var response = await ReadHeaderAsync(proxy.Stream, cancellationToken);
            var statusLine = Encoding.ASCII.GetString(response.Header).Split("\r\n", 2, StringSplitOptions.None)[0];
            var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var status) || status is < 200 or >= 300)
                throw new IOException($"Upstream HTTP proxy CONNECT failed: {statusLine}");

            proxy.InitialRead = response.Extra;
            return proxy;
        }
        catch
        {
            await proxy.DisposeAsync();
            throw;
        }
    }

    private async Task<OutboundConnection> OpenProxyTransportAsync(CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_upstream.Host, _upstream.Port, cancellationToken);
            Stream stream = tcp.GetStream();
            if (_upstream.Kind == ProxyKind.Https)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _upstream.Host,
                }, cancellationToken);
                stream = ssl;
            }
            return new OutboundConnection(tcp, stream);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task<OutboundConnection> OpenSocks5TunnelAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_upstream.Host, _upstream.Port, cancellationToken);
            var stream = tcp.GetStream();
            var hasCredentials = !string.IsNullOrEmpty(_upstream.Username) || !string.IsNullOrEmpty(_upstream.Password);
            var greeting = hasCredentials
                ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                : new byte[] { 0x05, 0x01, 0x00 };
            await stream.WriteAsync(greeting.AsMemory(), cancellationToken);

            var method = new byte[2];
            await ReadExactlyAsync(stream, method, cancellationToken);
            if (method[0] != 0x05 || method[1] == 0xff)
                throw new IOException("SOCKS5 proxy rejected the authentication methods offered by the launcher.");
            if (method[1] == 0x02)
                await AuthenticateSocks5Async(stream, cancellationToken);
            else if (method[1] != 0x00)
                throw new IOException($"SOCKS5 proxy selected unsupported authentication method 0x{method[1]:x2}.");

            var destination = await BuildSocksDestinationAsync(host, port, cancellationToken);
            var request = new byte[3 + destination.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            destination.CopyTo(request, 3);
            await stream.WriteAsync(request.AsMemory(), cancellationToken);

            var reply = new byte[4];
            await ReadExactlyAsync(stream, reply, cancellationToken);
            if (reply[0] != 0x05 || reply[1] != 0x00)
                throw new IOException($"SOCKS5 CONNECT failed with reply code 0x{reply[1]:x2}.");
            await ConsumeSocksAddressAsync(stream, reply[3], cancellationToken);
            var boundPort = new byte[2];
            await ReadExactlyAsync(stream, boundPort, cancellationToken);

            return new OutboundConnection(tcp, stream);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task AuthenticateSocks5Async(Stream stream, CancellationToken cancellationToken)
    {
        var user = Encoding.UTF8.GetBytes(_upstream.Username);
        var pass = Encoding.UTF8.GetBytes(_upstream.Password);
        var auth = new byte[3 + user.Length + pass.Length];
        auth[0] = 0x01;
        auth[1] = (byte)user.Length;
        user.CopyTo(auth, 2);
        auth[2 + user.Length] = (byte)pass.Length;
        pass.CopyTo(auth, 3 + user.Length);
        await stream.WriteAsync(auth.AsMemory(), cancellationToken);

        var reply = new byte[2];
        await ReadExactlyAsync(stream, reply, cancellationToken);
        if (reply[0] != 0x01 || reply[1] != 0x00)
            throw new IOException("SOCKS5 username/password authentication failed.");
    }

    private async Task<byte[]> BuildSocksDestinationAsync(string host, int port, CancellationToken cancellationToken)
    {
        byte[] address;
        byte atyp;

        if (IPAddress.TryParse(host, out var literal))
        {
            address = literal.GetAddressBytes();
            atyp = literal.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
        }
        else if (_upstream.Kind == ProxyKind.Socks5h)
        {
            var domain = Encoding.UTF8.GetBytes(host);
            if (domain.Length is < 1 or > 255) throw new IOException("SOCKS5 destination hostname is too long.");
            address = new byte[1 + domain.Length];
            address[0] = (byte)domain.Length;
            domain.CopyTo(address, 1);
            atyp = 0x03;
        }
        else
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var resolved = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6)
                ?? throw new IOException($"Could not resolve destination host '{host}'.");
            address = resolved.GetAddressBytes();
            atyp = resolved.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
        }

        var result = new byte[1 + address.Length + 2];
        result[0] = atyp;
        address.CopyTo(result, 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1 + address.Length, 2), checked((ushort)port));
        return result;
    }

    private static async Task ConsumeSocksAddressAsync(Stream stream, byte atyp, CancellationToken cancellationToken)
    {
        int length = atyp switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => -1,
            _ => throw new IOException($"SOCKS5 proxy returned unknown address type 0x{atyp:x2}."),
        };
        if (length == -1)
        {
            var size = new byte[1];
            await ReadExactlyAsync(stream, size, cancellationToken);
            length = size[0];
        }
        if (length > 0)
        {
            var discard = new byte[length];
            await ReadExactlyAsync(stream, discard, cancellationToken);
        }
    }

    private void AppendProxyAuthorization(StringBuilder builder)
    {
        if (string.IsNullOrEmpty(_upstream.Username) && string.IsNullOrEmpty(_upstream.Password)) return;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_upstream.Username}:{_upstream.Password}"));
        builder.Append("Proxy-Authorization: Basic ").Append(raw).Append("\r\n");
    }

    private byte[] BuildForwardHeader(ParsedRequest request, string target, bool includeProxyAuthorization)
    {
        var builder = new StringBuilder()
            .Append(request.Method).Append(' ').Append(target).Append(' ').Append(request.Version).Append("\r\n");

        foreach (var line in request.HeaderLines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            builder.Append(line).Append("\r\n");
        }

        if (includeProxyAuthorization) AppendProxyAuthorization(builder);
        builder.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static bool TryResolveHttpTarget(
        ParsedRequest request,
        out string host,
        out int port,
        out string originTarget,
        out string absoluteTarget)
    {
        host = string.Empty;
        port = 80;
        originTarget = request.Target;
        absoluteTarget = request.Target;

        if (Uri.TryCreate(request.Target, UriKind.Absolute, out var uri))
        {
            if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return false;
            host = uri.Host;
            port = uri.IsDefaultPort ? 80 : uri.Port;
            originTarget = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            absoluteTarget = uri.AbsoluteUri;
            return true;
        }

        var hostHeader = GetHeaderValue(request.HeaderLines, "Host");
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;
        (host, port) = ParseAuthority(hostHeader, 80);
        originTarget = request.Target.StartsWith('/') ? request.Target : "/" + request.Target;
        absoluteTarget = $"http://{FormatAuthority(host, port)}{originTarget}";
        return true;
    }

    private static string? GetHeaderValue(IEnumerable<string> lines, string name)
    {
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    private static ParsedRequest ParseRequest(byte[] headerBytes)
    {
        var text = Encoding.ASCII.GetString(headerBytes);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines.FirstOrDefault() ?? throw new IOException("Empty proxy request.");
        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new IOException("Malformed HTTP proxy request line.");
        return new ParsedRequest(parts[0], parts[1], parts[2], lines.Skip(1).Where(x => x.Length > 0).ToArray());
    }

    private static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        authority = authority.Trim();
        if (authority.StartsWith('['))
        {
            var closing = authority.IndexOf(']');
            if (closing < 0) throw new IOException("Malformed IPv6 proxy authority.");
            var host = authority[1..closing];
            if (closing + 1 == authority.Length) return (host, defaultPort);
            if (authority[closing + 1] != ':' || !int.TryParse(authority[(closing + 2)..], out var ipv6Port))
                throw new IOException("Malformed proxy authority port.");
            return (host, ValidatePort(ipv6Port));
        }

        var colon = authority.LastIndexOf(':');
        if (colon > 0 && authority.IndexOf(':') == colon)
        {
            if (!int.TryParse(authority[(colon + 1)..], out var parsedPort))
                throw new IOException("Malformed proxy authority port.");
            return (authority[..colon], ValidatePort(parsedPort));
        }
        if (string.IsNullOrWhiteSpace(authority)) throw new IOException("Proxy target host is empty.");
        return (authority, defaultPort);
    }

    private static int ValidatePort(int port) => port is >= 1 and <= 65535
        ? port
        : throw new IOException("Proxy target port is outside 1-65535.");

    private static string FormatAuthority(string host, int port)
        => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    private static async Task<(byte[] Header, byte[] Extra)> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        while (data.Length <= MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Connection closed before an HTTP header was complete.");
            data.Write(buffer, 0, read);
            var bytes = data.ToArray();
            var end = FindHeaderEnd(bytes);
            if (end >= 0)
            {
                var headerLength = end + 4;
                return (bytes[..headerLength], bytes[headerLength..]);
            }
        }
        throw new IOException("HTTP proxy header exceeded 64 KiB.");
    }

    private static int FindHeaderEnd(byte[] data)
    {
        for (var i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Proxy connection closed unexpectedly.");
            offset += read;
        }
    }

    private static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken);
    }

    private static async Task RelayBothWaysAsync(Stream left, Stream right, CancellationToken cancellationToken)
    {
        using var relayLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leftToRight = CopyIgnoringDisconnectAsync(left, right, relayLifetime.Token);
        var rightToLeft = CopyIgnoringDisconnectAsync(right, left, relayLifetime.Token);
        await Task.WhenAny(leftToRight, rightToLeft);
        relayLifetime.Cancel();
        try { await Task.WhenAll(leftToRight, rightToLeft); } catch (OperationCanceledException) { }
    }

    private static async Task CopyIgnoringDisconnectAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, 32 * 1024, cancellationToken);
            try { await destination.FlushAsync(cancellationToken); } catch { }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }

    private sealed record ParsedRequest(string Method, string Target, string Version, string[] HeaderLines);

    private sealed class OutboundConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;

        public OutboundConnection(TcpClient client, Stream stream)
        {
            _client = client;
            Stream = stream;
        }

        public Stream Stream { get; }
        public byte[] InitialRead { get; set; } = Array.Empty<byte>();

        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync(); } finally { _client.Dispose(); }
        }
    }
}
