using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodexTimeZoneLauncher.Models;

namespace CodexTimeZoneLauncher.Services;

public sealed class LaunchCoordinator
{
    private readonly ElectronTimeZoneBootstrapper _bootstrapper = new();

    public async Task<LaunchResult> LaunchAsync(
        CodexInstallation installation,
        ResolvedTimeZone timezone,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var running = RunningCodexGuard.FindMatchingProcesses(installation.ExecutablePath);
        if (running.Count > 0)
        {
            throw new InvalidOperationException(
                $"Codex is already running from the discovered package (PID {string.Join(", ", running)}). Close it normally before launching. No process was stopped.");
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

        log($"Launching {installation.PackageFullName}");
        log($"Timezone: {timezone.IanaId} / {timezone.WindowsId}");
        log($"Inspector: 127.0.0.1:{inspectorPort} (loopback only)");

        var startInfo = new ProcessStartInfo
        {
            FileName = nativeLauncher,
            WorkingDirectory = baseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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
        return new LaunchResult(timezone, shimLog, inspectorPort);
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
