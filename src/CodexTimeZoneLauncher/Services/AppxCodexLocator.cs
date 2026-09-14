using System.Diagnostics;
using System.Text.Json;
using CodexTimeZoneLauncher.Models;

namespace CodexTimeZoneLauncher.Services;

public sealed class AppxCodexLocator
{
    private sealed record AppxInfo(string PackageFullName, string Version, string InstallLocation);

    public async Task<CodexInstallation> LocateAsync(CancellationToken cancellationToken)
    {
        const string script = "$p = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1; if ($null -eq $p) { exit 3 }; [pscustomobject]@{ PackageFullName = [string]$p.PackageFullName; Version = $p.Version.ToString(); InstallLocation = [string]$p.InstallLocation } | ConvertTo-Json -Compress";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Windows PowerShell for AppX discovery.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.ExitCode == 3
                ? "OpenAI.Codex is not installed for the current Windows user."
                : $"AppX discovery failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }

        var info = JsonSerializer.Deserialize<AppxInfo>(stdout.Trim())
            ?? throw new InvalidOperationException("AppX discovery returned invalid JSON.");
        var executable = Path.GetFullPath(Path.Combine(info.InstallLocation, "app", "ChatGPT.exe"));
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The current OpenAI.Codex package does not contain app\\ChatGPT.exe.", executable);
        }
        return new CodexInstallation(info.PackageFullName, info.Version, info.InstallLocation, executable);
    }
}
