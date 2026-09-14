namespace CodexTimeZoneLauncher.Models;

public sealed class LauncherSettings
{
    public string TimeZone { get; set; } = "Pacific/Honolulu";
    public bool ProxyEnabled { get; set; }
    public string ProxyMode { get; set; } = "HTTP";
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUsername { get; set; } = string.Empty;
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

public enum ProxyKind
{
    Direct,
    Http,
    Https,
    Socks5,
    Socks5h,
}

public sealed record ProxyLaunchConfiguration(
    ProxyKind Kind,
    string Host,
    int Port,
    string Username,
    string Password)
{
    public bool Enabled => Kind != ProxyKind.Direct;

    public string SafeDisplay => Kind switch
    {
        ProxyKind.Direct => "Direct",
        ProxyKind.Http => $"HTTP {Host}:{Port}",
        ProxyKind.Https => $"HTTPS {Host}:{Port}",
        ProxyKind.Socks5 => $"SOCKS5 {Host}:{Port} (local DNS)",
        ProxyKind.Socks5h => $"SOCKS5h {Host}:{Port} (proxy DNS)",
        _ => Kind.ToString(),
    };
}

public sealed record LaunchResult(
    ResolvedTimeZone TimeZone,
    string ShimLogPath,
    int InspectorPort,
    int? LocalProxyPort = null);
