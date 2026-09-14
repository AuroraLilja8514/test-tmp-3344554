using System.Net.Http.Json;
using System.Text.Json;

namespace CodexTimeZoneLauncher.Services;

public sealed class ElectronTimeZoneBootstrapper
{
    private sealed class Capabilities
    {
        public bool HasProcess { get; set; }
        public bool HasRequire { get; set; }
        public bool HasBuiltinModule { get; set; }
    }

    private sealed class RendererValue
    {
        public string? TimeZone { get; set; }
        public int OffsetMinutes { get; set; }
        public string? DateString { get; set; }
    }

    private sealed class Observation
    {
        public int Id { get; set; }
        public string? Url { get; set; }
        public string? Type { get; set; }
        public RendererValue? Value { get; set; }
        public string? Error { get; set; }
    }

    private sealed class Snapshot
    {
        public List<Observation>? Output { get; set; }
    }

    private sealed class InstallResult
    {
        public bool Ok { get; set; }
        public string? Timezone { get; set; }
    }

    private static readonly string CapabilityExpression = """
(() => ({
  hasProcess: typeof process !== 'undefined',
  hasRequire: typeof require === 'function',
  hasBuiltinModule: typeof process !== 'undefined' && typeof process.getBuiltinModule === 'function'
}))()
""";

    private static string BuildBootstrap(string timezone)
    {
        var expression = """
(() => {
  const timezone = __TZ__;
  const getRequire = () => {
    if (typeof require === 'function') return require;
    if (typeof process !== 'undefined' && typeof process.getBuiltinModule === 'function') {
      const mod = process.getBuiltinModule('module');
      if (mod && typeof mod.createRequire === 'function') return mod.createRequire(process.execPath);
    }
    throw new Error('Node require() is unavailable in the selected paused call frame');
  };

  const electron = getRequire()('electron');
  const app = electron.app;
  const webContents = electron.webContents;
  const state = globalThis.__codexTimezoneLauncher || { applied: {}, errors: [], retries: {} };
  state.timezone = timezone;
  globalThis.__codexTimezoneLauncher = state;

  const recordError = (wc, stage, error) => {
    state.errors.push({ id: wc && wc.id, stage, message: String(error && (error.stack || error.message) || error), at: Date.now() });
    if (state.errors.length > 100) state.errors.splice(0, state.errors.length - 100);
  };

  const apply = async (wc) => {
    if (!wc || wc.isDestroyed()) return false;
    try {
      if (!wc.debugger.isAttached()) wc.debugger.attach('1.3');
      await wc.debugger.sendCommand('Emulation.setTimezoneOverride', { timezoneId: timezone });
      state.applied[wc.id] = { url: wc.getURL(), at: Date.now() };
      return true;
    } catch (error) {
      recordError(wc, 'apply', error);
      return false;
    }
  };

  const applyWithRetry = (wc, remaining = 120) => {
    if (!wc || wc.isDestroyed() || remaining <= 0) return;
    Promise.resolve(apply(wc)).then((ok) => {
      if (!ok && !wc.isDestroyed()) {
        state.retries[wc.id] = (state.retries[wc.id] || 0) + 1;
        setTimeout(() => applyWithRetry(wc, remaining - 1), 100);
      }
    }).catch((error) => recordError(wc, 'retry', error));
  };

  const kick = (wc) => setImmediate(() => applyWithRetry(wc));
  const installFor = (wc) => {
    if (!wc || wc.isDestroyed() || wc.__codexTimezoneLauncherInstalled) return;
    wc.__codexTimezoneLauncherInstalled = true;
    kick(wc);
    wc.on('did-start-navigation', () => kick(wc));
    wc.on('did-finish-load', () => kick(wc));
    wc.on('render-process-gone', () => { delete state.applied[wc.id]; });
  };

  if (!state.listenerInstalled) {
    state.listenerInstalled = true;
    app.on('web-contents-created', (_event, wc) => installFor(wc));
  }

  setImmediate(() => {
    try { for (const wc of webContents.getAllWebContents()) installFor(wc); }
    catch (error) { recordError(null, 'initial-sweep', error); }
  });

  state.inspect = async () => {
    const output = [];
    for (const wc of webContents.getAllWebContents()) {
      if (!wc || wc.isDestroyed()) continue;
      try {
        const value = await wc.executeJavaScript(`(() => ({
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          offsetMinutes: new Date().getTimezoneOffset(),
          dateString: new Date().toString()
        }))()`, true);
        output.push({ id: wc.id, url: wc.getURL(), type: wc.getType(), value });
      } catch (error) {
        output.push({ id: wc.id, url: wc.getURL(), type: wc.getType(), error: String(error) });
      }
    }
    return { output };
  };

  return { ok: true, timezone };
})()
""";
        return expression.Replace("__TZ__", JsonSerializer.Serialize(timezone), StringComparison.Ordinal);
    }

    public async Task InstallAndVerifyAsync(int port, string timezone, TimeSpan timeout, Action<string> log, CancellationToken cancellationToken)
    {
        log($"Waiting for Electron inspector on 127.0.0.1:{port} ...");
        var target = await WaitForTargetAsync(port, timeout, cancellationToken);
        log("Electron inspector is reachable.");

        await using var client = new InspectorClient();
        var resumed = false;
        try
        {
            await client.ConnectAsync(new Uri(target), cancellationToken);
            await client.CommandAsync("Runtime.enable", null, TimeSpan.FromSeconds(5), cancellationToken);
            await client.CommandAsync("Debugger.enable", null, TimeSpan.FromSeconds(5), cancellationToken);
            await client.CommandAsync("Runtime.runIfWaitingForDebugger", null, TimeSpan.FromSeconds(10), cancellationToken);
            var paused = await client.WaitForEventAsync("Debugger.paused", TimeSpan.FromSeconds(20), cancellationToken);
            log("Electron main JavaScript reached the --inspect-brk startup pause.");

            var frames = paused.GetProperty("params").GetProperty("callFrames").EnumerateArray().ToArray();
            string? frameId = null;
            foreach (var frame in frames)
            {
                if (!frame.TryGetProperty("callFrameId", out var idElement)) continue;
                var id = idElement.GetString();
                if (string.IsNullOrEmpty(id)) continue;
                try
                {
                    var caps = await client.EvaluateOnFrameAsync<Capabilities>(id, CapabilityExpression, TimeSpan.FromSeconds(5), cancellationToken);
                    if (caps?.HasProcess == true && (caps.HasRequire || caps.HasBuiltinModule))
                    {
                        frameId = id;
                        break;
                    }
                }
                catch { }
            }
            if (frameId is null) throw new InvalidOperationException("No paused Node call frame exposed require/process capabilities.");

            log("Installing the persistent per-WebContents timezone hook ...");
            var installed = await client.EvaluateOnFrameAsync<InstallResult>(frameId, BuildBootstrap(timezone), TimeSpan.FromSeconds(15), cancellationToken);
            if (installed?.Ok != true) throw new InvalidOperationException("Electron timezone bootstrap returned an unexpected result.");

            log("Resuming Codex startup ...");
            await client.CommandAsync("Debugger.resume", null, TimeSpan.FromSeconds(8), cancellationToken);
            resumed = true;

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, cancellationToken);
                Snapshot? snapshot;
                try
                {
                    snapshot = await client.EvaluateGlobalAsync<Snapshot>(
                        "globalThis.__codexTimezoneLauncher && globalThis.__codexTimezoneLauncher.inspect ? globalThis.__codexTimezoneLauncher.inspect() : null",
                        TimeSpan.FromSeconds(8), cancellationToken);
                }
                catch
                {
                    continue;
                }

                var matching = snapshot?.Output?.FirstOrDefault(o =>
                    o.Value?.TimeZone is string observed && ZoneEquals(observed, timezone));
                if (matching?.Value is not null)
                {
                    log($"Renderer verified: {matching.Value.TimeZone}; offset minutes {matching.Value.OffsetMinutes}.");
                    return;
                }
            }
            throw new TimeoutException($"No Chromium renderer reported '{timezone}' before the verification timeout.");
        }
        finally
        {
            if (!resumed)
            {
                try { await client.CommandAsync("Runtime.runIfWaitingForDebugger", null, TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
                try { await client.CommandAsync("Debugger.resume", null, TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
            }
        }
    }

    private static bool ZoneEquals(string left, string right) =>
        string.Equals(left.Trim().Replace('_', '-'), right.Trim().Replace('_', '-'), StringComparison.OrdinalIgnoreCase);

    private static async Task<string> WaitForTargetAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/json/list", cancellationToken);
                response.EnsureSuccessStatusCode();
                var targets = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                if (targets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var target in targets.EnumerateArray())
                    {
                        if (target.TryGetProperty("webSocketDebuggerUrl", out var ws) && ws.GetString() is { Length: > 0 } url)
                            return url;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = ex;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"Electron inspector did not become ready. {last?.Message}");
    }
}
