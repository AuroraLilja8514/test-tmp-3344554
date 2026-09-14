using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodexTimeZoneLauncher.Models;
using CodexTimeZoneLauncher.Services;

static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
        if (read == 0) throw new EndOfStreamException("Connection closed unexpectedly.");
        offset += read;
    }
}

static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
{
    using var data = new MemoryStream();
    var one = new byte[1];
    while (data.Length < 64 * 1024)
    {
        var read = await stream.ReadAsync(one.AsMemory(), cancellationToken);
        if (read == 0) break;
        data.WriteByte(one[0]);
        var bytes = data.GetBuffer();
        var len = (int)data.Length;
        if (len >= 4 && bytes[len - 4] == '\r' && bytes[len - 3] == '\n' && bytes[len - 2] == '\r' && bytes[len - 1] == '\n')
            return Encoding.ASCII.GetString(bytes, 0, len);
    }
    throw new IOException("Header was not completed.");
}

static async Task<string> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
{
    using var data = new MemoryStream();
    var buffer = new byte[4096];
    while (true)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (read == 0) break;
        data.Write(buffer, 0, read);
    }
    return Encoding.ASCII.GetString(data.ToArray());
}

static (TcpListener Listener, int Port, Task ServerTask) StartHttpOrigin(CancellationToken cancellationToken)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var task = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var request = await ReadHeaderAsync(stream, cancellationToken);
        if (!request.StartsWith("GET /proxy-smoke HTTP/1.1", StringComparison.Ordinal))
            throw new Exception("Origin received an unexpected request line: " + request.Split("\r\n")[0]);
        var body = Encoding.ASCII.GetBytes("OK");
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response.AsMemory(), cancellationToken);
        await stream.WriteAsync(body.AsMemory(), cancellationToken);
    }, cancellationToken);
    return (listener, port, task);
}

static (TcpListener Listener, int Port, Task ServerTask) StartEchoOrigin(CancellationToken cancellationToken)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var task = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var request = new byte[4];
        await ReadExactlyAsync(stream, request, cancellationToken);
        if (Encoding.ASCII.GetString(request) != "PING") throw new IOException("Unexpected echo payload.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("PONG").AsMemory(), cancellationToken);
    }, cancellationToken);
    return (listener, port, task);
}

static (TcpListener Listener, int Port, Task ServerTask) StartAuthenticatedSocks5hProxy(CancellationToken cancellationToken)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var task = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();

        var greetingHead = new byte[2];
        await ReadExactlyAsync(stream, greetingHead, cancellationToken);
        if (greetingHead[0] != 0x05) throw new Exception("SOCKS client did not send version 5 greeting.");
        var methods = new byte[greetingHead[1]];
        await ReadExactlyAsync(stream, methods, cancellationToken);
        if (!methods.Contains((byte)0x02)) throw new Exception("SOCKS client did not offer username/password authentication.");
        await stream.WriteAsync(new byte[] { 0x05, 0x02 }, cancellationToken);

        var authHead = new byte[2];
        await ReadExactlyAsync(stream, authHead, cancellationToken);
        if (authHead[0] != 0x01) throw new Exception("Unexpected SOCKS authentication version.");
        var usernameBytes = new byte[authHead[1]];
        await ReadExactlyAsync(stream, usernameBytes, cancellationToken);
        var passwordLength = new byte[1];
        await ReadExactlyAsync(stream, passwordLength, cancellationToken);
        var passwordBytes = new byte[passwordLength[0]];
        await ReadExactlyAsync(stream, passwordBytes, cancellationToken);
        if (Encoding.UTF8.GetString(usernameBytes) != "smoke-user" || Encoding.UTF8.GetString(passwordBytes) != "smoke-pass")
            throw new Exception("SOCKS credentials did not reach the upstream proxy correctly.");
        await stream.WriteAsync(new byte[] { 0x01, 0x00 }, cancellationToken);

        var connectHead = new byte[4];
        await ReadExactlyAsync(stream, connectHead, cancellationToken);
        if (connectHead[0] != 0x05 || connectHead[1] != 0x01 || connectHead[3] != 0x03)
            throw new Exception("SOCKS5h request did not use a remote-DNS domain CONNECT.");
        var domainLength = new byte[1];
        await ReadExactlyAsync(stream, domainLength, cancellationToken);
        var domainBytes = new byte[domainLength[0]];
        await ReadExactlyAsync(stream, domainBytes, cancellationToken);
        var portBytes = new byte[2];
        await ReadExactlyAsync(stream, portBytes, cancellationToken);
        var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        if (Encoding.UTF8.GetString(domainBytes) != "example.internal" || destinationPort != 443)
            throw new Exception("SOCKS5h destination was not preserved for remote DNS.");

        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }, cancellationToken);
        var ping = new byte[4];
        await ReadExactlyAsync(stream, ping, cancellationToken);
        if (Encoding.ASCII.GetString(ping) != "PING") throw new Exception("SOCKS5h tunnel did not carry PING.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("PONG").AsMemory(), cancellationToken);
    }, cancellationToken);
    return (listener, port, task);
}

static async Task AssertConnectTunnelAsync(int adapterPort, string authority, CancellationToken cancellationToken)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, adapterPort, cancellationToken);
    using var stream = client.GetStream();
    var connect = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
    await stream.WriteAsync(connect.AsMemory(), cancellationToken);
    var responseHeader = await ReadHeaderAsync(stream, cancellationToken);
    if (!responseHeader.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
        throw new Exception("CONNECT proxy test did not return 200: " + responseHeader.Split("\r\n")[0]);
    await stream.WriteAsync(Encoding.ASCII.GetBytes("PING").AsMemory(), cancellationToken);
    var reply = new byte[4];
    await ReadExactlyAsync(stream, reply, cancellationToken);
    if (Encoding.ASCII.GetString(reply) != "PONG") throw new Exception("CONNECT tunnel did not relay PONG.");
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

var direct = new ProxyLaunchConfiguration(ProxyKind.Direct, string.Empty, 0, string.Empty, string.Empty);
await using (var adapter = new LoopbackProxyAdapter(direct, message => Console.WriteLine("proxy: " + message)))
{
    await adapter.StartAsync(timeout.Token);

    var (httpOrigin, httpPort, httpServer) = StartHttpOrigin(timeout.Token);
    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, adapter.Port, timeout.Token);
        using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{httpPort}/proxy-smoke HTTP/1.1\r\nHost: 127.0.0.1:{httpPort}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request.AsMemory(), timeout.Token);
        var response = await ReadToEndAsync(stream, timeout.Token);
        if (!response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) || !response.EndsWith("OK", StringComparison.Ordinal))
            throw new Exception("Plain HTTP proxy test returned an unexpected response.");
        await httpServer;
        Console.WriteLine("HTTP_PROXY_SMOKE_PASS");
    }
    finally
    {
        httpOrigin.Stop();
    }

    var (echoOrigin, echoPort, echoServer) = StartEchoOrigin(timeout.Token);
    try
    {
        await AssertConnectTunnelAsync(adapter.Port, $"127.0.0.1:{echoPort}", timeout.Token);
        await echoServer;
        Console.WriteLine("CONNECT_PROXY_SMOKE_PASS");
    }
    finally
    {
        echoOrigin.Stop();
    }
}

var (socksProxy, socksPort, socksServer) = StartAuthenticatedSocks5hProxy(timeout.Token);
try
{
    var socks = new ProxyLaunchConfiguration(ProxyKind.Socks5h, "127.0.0.1", socksPort, "smoke-user", "smoke-pass");
    await using var socksAdapter = new LoopbackProxyAdapter(socks, message => Console.WriteLine("proxy: " + message));
    await socksAdapter.StartAsync(timeout.Token);
    await AssertConnectTunnelAsync(socksAdapter.Port, "example.internal:443", timeout.Token);
    await socksServer;
    Console.WriteLine("SOCKS5H_PROXY_SMOKE_PASS");
}
finally
{
    socksProxy.Stop();
}

Console.WriteLine("PROXY_SMOKE_PASS");
