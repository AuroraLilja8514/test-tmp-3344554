using System.Text;
using CodexTimeZoneLauncher.Models;

namespace CodexTimeZoneLauncher.Services;

public static class ProxyConfigurationParser
{
    public static ProxyLaunchConfiguration Resolve(
        bool enabled,
        string modeText,
        string hostText,
        string portText,
        string username,
        string password)
    {
        if (!enabled)
            return new ProxyLaunchConfiguration(ProxyKind.Direct, string.Empty, 0, string.Empty, string.Empty);

        var kind = NormalizeMode(modeText);
        var host = NormalizeHost(hostText);
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Proxy host is required when proxy routing is enabled.");

        if (!int.TryParse(portText.Trim(), out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Proxy port must be a number from 1 to 65535.");

        username ??= string.Empty;
        password ??= string.Empty;
        RejectNewlines(username, "Proxy username");
        RejectNewlines(password, "Proxy password");

        if (kind is ProxyKind.Socks5 or ProxyKind.Socks5h)
        {
            if (Encoding.UTF8.GetByteCount(username) > 255)
                throw new ArgumentException("SOCKS5 username must be at most 255 UTF-8 bytes.");
            if (Encoding.UTF8.GetByteCount(password) > 255)
                throw new ArgumentException("SOCKS5 password must be at most 255 UTF-8 bytes.");
        }

        return new ProxyLaunchConfiguration(kind, host, port, username, password);
    }

    public static ProxyKind NormalizeMode(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "http" => ProxyKind.Http,
            "https" => ProxyKind.Https,
            "socks5" => ProxyKind.Socks5,
            "socks5h" => ProxyKind.Socks5h,
            _ => throw new ArgumentException("Proxy type must be HTTP, HTTPS, SOCKS5, or SOCKS5h."),
        };
    }

    private static string NormalizeHost(string value)
    {
        var host = (value ?? string.Empty).Trim();
        RejectNewlines(host, "Proxy host");
        if (host.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Enter only the proxy host in the Host field; choose the protocol separately.");
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];
        return host;
    }

    private static void RejectNewlines(string value, string name)
    {
        if (value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException($"{name} cannot contain line breaks.");
    }
}
