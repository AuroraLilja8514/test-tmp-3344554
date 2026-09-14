using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodexTimeZoneLauncher.Models;

namespace CodexTimeZoneLauncher.Services;

public sealed class LaunchCoordinator : IAsyncDisposable
{
    private readonly ElectronTimeZoneBootstrapper _bootstrapper = new();
    private LoopbackProxyAdapter? _activeProxy;

    public bool HasActiveProxy => _activeProxy is not null;

    public async Task<LaunchResult> LaunchAsync(
        CodexInstallation installation,
        ResolvedTimeZone timezone,
        ProxyLaunchConfiguration proxy,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var running = RunningCodexGuard.FindMatchingProcesses(installation.ExecutablePath);
        if (running.Count > 0)
        {
            throw new InvalidOperationException(
                $"Codex is already running from the discovered package (PID {string.Join(", ", running)}). Close it normally before launching. No process was stopped.");
        }

        if (_activeProxy is not null)
        {
            await _activeProxy.DisposeAsync();
            _activeProxy = null;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var nativeLauncher = Path.Combine(baseDirectory, "tzshim-launcher.exe");
        var broker = Path.Combine(baseDirectory, "CodexTzBroker64.dll");
        var shim = Path.Combine(baseDirectory, "CodexTzShim64.dll");
        foreach (var path in new[] { nativeLauncher, broker, shim })
        {
            if (!File.Exists(path)) throw new FileNotFoundException("A required native helper is missing from the launcher folder.", path);
        }

        var inspectorPort = AllocateLoopbackPort();
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTimeZoneLauncher", "logs");
        Directory.CreateDirectory(logDirectory);
        var shimLog = Path.Combine(logDirectory, $"tzshim-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

        LoopbackProxyAdapter? startingProxy = null;
        try
        {
            if (proxy.Enabled)
            {
                startingProxy = new LoopbackProxyAdapter(proxy, log);
                await startingProxy.StartAsync(cancellationToken);
            }

            log($"Launching {installation.PackageFullName}");
            log($"Timezone: {timezone.IanaId} / {timezone.WindowsId}");
            log($"Inspector: 127.0.0.1:{inspectorPort} (loopback only)");
            log(proxy.Enabled ? $"Proxy route: {proxy.SafeDisplay}" : "Proxy route: Direct");

            var startInfo = new ProcessStartInfo
            {
                FileName = nativeLauncher,
                WorkingDirectory = baseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (startingProxy is not null)
            {
                var localProxy = $"http://127.0.0.1:{startingProxy.Port}";
                foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                    startInfo.Environment[name] = localProxy;
                foreach (var name in new[] { "NO_PROXY", "no_proxy" })
                    startInfo.Environment[name] = "localhost,127.0.0.1,::1";
            }

            startInfo.ArgumentList.Add("--timezone-windows-id");
            startInfo.ArgumentList.Add(timezone.WindowsId);
            startInfo.ArgumentList.Add("--timezone-iana");
            startInfo.ArgumentList.Add(timezone.IanaId);
            startInfo.ArgumentList.Add("--dll");
            startInfo.ArgumentList.Add("CodexTzBroker64.dll");
            startInfo.ArgumentList.Add("--log");
            startInfo.ArgumentList.Add(shimLog);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(installation.ExecutablePath);
            startInfo.ArgumentList.Add($"--inspect-brk=127.0.0.1:{inspectorPort}");

            if (startingProxy is not null)
            {
                startInfo.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{startingProxy.Port}");
                startInfo.ArgumentList.Add("--proxy-bypass-list=localhost;127.0.0.1;[::1]");
            }

            // Never redirect stdout/stderr here. A long-lived GUI child can inherit redirected pipe
            // handles and keep the parent waiting for EOF even after tzshim-launcher.exe exits.
            using (var launcher = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the native Codex broker launcher."))
            {
                await launcher.WaitForExitAsync(cancellationToken);
                if (launcher.ExitCode != 0)
                    throw new InvalidOperationException($"Native launcher failed with exit code {launcher.ExitCode}.");
            }

            await _bootstrapper.InstallAndVerifyAsync(inspectorPort, timezone.IanaId, TimeSpan.FromSeconds(60), log, cancellationToken);
            log("Chromium timezone verified. Native app-server broker remains active for future codex.exe app-server children.");

            if (startingProxy is not null)
            {
                _activeProxy = startingProxy;
                startingProxy = null;
                log("Proxy adapter will remain active while this launcher stays open.");
            }

            return new LaunchResult(timezone, shimLog, inspectorPort, _activeProxy?.Port);
        }
        catch
        {
            if (startingProxy is not null) await startingProxy.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_activeProxy is not null)
        {
            await _activeProxy.DisposeAsync();
            _activeProxy = null;
        }
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
