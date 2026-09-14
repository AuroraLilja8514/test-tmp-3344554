using System.Text.RegularExpressions;
using CodexTimeZoneLauncher.Models;

namespace CodexTimeZoneLauncher.Services;

public static partial class TimeZoneResolver
{
    [GeneratedRegex("^UTC(?<sign>[+-])(?<hours>\\d{1,2})(?::?(?<minutes>\\d{2}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FixedOffsetRegex();

    public static IReadOnlyList<string> GetIanaCatalog()
    {
        var zones = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "UTC" };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && !string.IsNullOrWhiteSpace(iana))
            {
                zones.Add(iana);
            }
        }
        return zones.ToArray();
    }

    public static ResolvedTimeZone Resolve(string value)
    {
        var input = value.Trim();
        if (input.Length == 0)
        {
            throw new ArgumentException("Choose a timezone before launching Codex.");
        }

        if (string.Equals(input, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedTimeZone("UTC", "UTC");
        }

        var fixedMatch = FixedOffsetRegex().Match(input);
        if (fixedMatch.Success)
        {
            var hours = int.Parse(fixedMatch.Groups["hours"].Value);
            var minutesText = fixedMatch.Groups["minutes"].Value;
            var minutes = minutesText.Length == 0 ? 0 : int.Parse(minutesText);
            if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0))
            {
                throw new ArgumentException("Fixed offsets must be between UTC-14:00 and UTC+14:00.");
            }
            var offset = new TimeSpan(hours, minutes, 0);
            if (fixedMatch.Groups["sign"].Value == "-") offset = -offset;
            if (offset == TimeSpan.Zero) return new ResolvedTimeZone("UTC", "UTC", true, offset);
            throw new NotSupportedException(
                "Non-zero fixed UTC offsets are recognized, but the production native backend does not synthesize fixed-offset Windows rules yet. Use an IANA timezone for this build.");
        }

        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(input, out var windowsId) || string.IsNullOrWhiteSpace(windowsId))
        {
            throw new ArgumentException($"'{input}' is not an IANA timezone understood by this Windows/.NET runtime.");
        }
        _ = TimeZoneInfo.FindSystemTimeZoneById(windowsId);

        var canonicalIana = input;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var converted) && !string.IsNullOrWhiteSpace(converted))
        {
            canonicalIana = converted;
        }
        return new ResolvedTimeZone(canonicalIana, windowsId);
    }
}
