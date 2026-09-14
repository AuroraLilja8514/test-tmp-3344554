using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CodexTimeZoneLauncher.Services;

internal sealed class InspectorClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly List<JsonElement> _events = [];
    private int _nextId;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public async Task<JsonElement> CommandAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
        if (parameters is not null) payload["params"] = parameters;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            var message = await ReceiveAsync(timeoutCts.Token);
            if (message.TryGetProperty("id", out var messageId) && messageId.GetInt32() == id)
            {
                if (message.TryGetProperty("error", out var error))
                    throw new InvalidOperationException($"{method} failed: {error}");
                return message.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
            _events.Add(message.Clone());
        }
    }

    public async Task<JsonElement> WaitForEventAsync(string method, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var existing = _events.FindIndex(e => e.TryGetProperty("method", out var m) && m.GetString() == method);
        if (existing >= 0)
        {
            var found = _events[existing];
            _events.RemoveAt(existing);
            return found;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            var message = await ReceiveAsync(timeoutCts.Token);
            if (message.TryGetProperty("method", out var m) && m.GetString() == method) return message;
            _events.Add(message.Clone());
        }
    }

    public async Task<T?> EvaluateOnFrameAsync<T>(string frameId, string expression, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await CommandAsync("Debugger.evaluateOnCallFrame", new
        {
            callFrameId = frameId,
            expression,
            returnByValue = true,
            silent = false,
        }, timeout, cancellationToken);
        return RemoteValue<T>(result, "Debugger.evaluateOnCallFrame");
    }

    public async Task<T?> EvaluateGlobalAsync<T>(string expression, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await CommandAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
        }, timeout, cancellationToken);
        return RemoteValue<T>(result, "Runtime.evaluate");
    }

    private static T? RemoteValue<T>(JsonElement result, string method)
    {
        if (result.ValueKind == JsonValueKind.Undefined) return default;
        if (result.TryGetProperty("exceptionDetails", out var details))
            throw new InvalidOperationException($"{method} raised an exception: {details}");
        if (!result.TryGetProperty("result", out var remote) || !remote.TryGetProperty("value", out var value)) return default;
        return value.Deserialize<T>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private async Task<JsonElement> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Electron inspector connection closed.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch { }
        }
        _socket.Dispose();
    }
}
