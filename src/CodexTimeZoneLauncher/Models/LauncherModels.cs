namespace CodexTimeZoneLauncher.Models;

public sealed class LauncherSettings
{
    public string TimeZone { get; set; } = "Pacific/Honolulu";
}

public sealed record CodexInstallation(
    string PackageFullName,
    string Version,
    string InstallLocation,
    string ExecutablePath);

public sealed record ResolvedTimeZone(
    string IanaId,
    string WindowsId,
    bool IsFixedOffset = false,
    TimeSpan? FixedOffset = null);

public sealed record LaunchResult(
    ResolvedTimeZone TimeZone,
    string ShimLogPath,
    int InspectorPort);
