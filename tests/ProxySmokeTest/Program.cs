using System.Net;
using System.Net.Sockets;
using System.Text;
using CodexTimeZoneLauncher.Models;
using CodexTimeZoneLauncher.Services;

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

static async Task<(TcpListener Listener, int Port)> StartHttpOriginAsync(CancellationToken cancellationToken)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        _ = await ReadHeaderAsync(stream, cancellationToken);
        var body = Encoding.ASCII.GetBytes("OK");
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response.AsMemory(), cancellationToken);
        await stream.WriteAsync(body.AsMemory(), cancellationToken);
    }, cancellationToken);
    return (listener, port);
}

static async Task<(TcpListener Listener, int Port)> StartEchoOriginAsync(CancellationToken cancellationToken)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var request = new byte[4];
        var offset = 0;
        while (offset < request.Length)
        {
            var read = await stream.ReadAsync(request.AsMemory(offset), cancellationToken);
            if (read == 0) throw new IOException("Echo client disconnected early.");
            offset += read;
        }
        if (Encoding.ASCII.GetString(request) != "PING") throw new IOException("Unexpected echo payload.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("PONG").AsMemory(), cancellationToken);
    }, cancellationToken);
    return (listener, port);
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var direct = new ProxyLaunchConfiguration(ProxyKind.Direct, string.Empty, 0, string.Empty, string.Empty);
await using var adapter = new LoopbackProxyAdapter(direct, message => Console.WriteLine("proxy: " + message));
await adapter.StartAsync(timeout.Token);

var (httpOrigin, httpPort) = await StartHttpOriginAsync(timeout.Token);
try
{
    using var handler = new SocketsHttpHandler
    {
        UseProxy = true,
        Proxy = new WebProxy($"http://127.0.0.1:{adapter.Port}", bypassOnLocal: false),
    };
    using var http = new HttpClient(handler);
    var text = await http.GetStringAsync($"http://127.0.0.1:{httpPort}/proxy-smoke", timeout.Token);
    if (text != "OK") throw new Exception($"Plain HTTP proxy test returned '{text}'.");
    Console.WriteLine("HTTP_PROXY_SMOKE_PASS");
}
finally
{
    httpOrigin.Stop();
}

var (echoOrigin, echoPort) = await StartEchoOriginAsync(timeout.Token);
try
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, adapter.Port, timeout.Token);
    using var stream = client.GetStream();
    var connect = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");
    await stream.WriteAsync(connect.AsMemory(), timeout.Token);
    var responseHeader = await ReadHeaderAsync(stream, timeout.Token);
    if (!responseHeader.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
        throw new Exception("CONNECT proxy test did not return 200: " + responseHeader.Split("\r\n")[0]);
    await stream.WriteAsync(Encoding.ASCII.GetBytes("PING").AsMemory(), timeout.Token);
    var reply = new byte[4];
    var offset = 0;
    while (offset < reply.Length)
    {
        var read = await stream.ReadAsync(reply.AsMemory(offset), timeout.Token);
        if (read == 0) throw new IOException("CONNECT tunnel closed before PONG.");
        offset += read;
    }
    if (Encoding.ASCII.GetString(reply) != "PONG") throw new Exception("CONNECT tunnel did not relay PONG.");
    Console.WriteLine("CONNECT_PROXY_SMOKE_PASS");
}
finally
{
    echoOrigin.Stop();
}

Console.WriteLine("PROXY_SMOKE_PASS");
