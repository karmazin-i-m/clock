using System.Collections.Concurrent;
using System.Text;

namespace KClock.Api.Devices;

/// <summary>
/// The POSIX TZ rule for an IANA zone — what the ESP's configTzTime() takes, e.g.
/// "EET-2EEST,M3.5.0/3,M10.5.0/4" for Europe/Kyiv. Read from the system tzdata rather than
/// kept in a table here: every TZif file since version 2 ends with exactly this rule as its
/// footer line, maintained by the people who maintain the zones. The API host installs tzdata
/// for precisely this (Server/README.md).
///
/// Null when the zone has no file, no footer, or a rule the ESP cannot hold — its settings.tz
/// is 48 bytes — in which case the config is sent without "tz" and the clock keeps its own.
/// </summary>
public static class PosixTimezone
{
    private const int EspMaxLength = 47;

    private static readonly string ZoneInfo = Environment.GetEnvironmentVariable("TZDIR") ?? "/usr/share/zoneinfo";
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);

    public static string? For(string ianaId) => Cache.GetOrAdd(ianaId, Read);

    private static string? Read(string ianaId)
    {
        // The id already passed IsIanaTimezone on the way in; this only keeps a bad one from
        // ever becoming a path outside the zone directory.
        if (ianaId.Length == 0 || ianaId.Contains("..", StringComparison.Ordinal) || ianaId.StartsWith('/'))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(ZoneInfo, ianaId));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.Length < 6 || bytes[^1] != (byte)'\n' || bytes[0..4] is not [(byte)'T', (byte)'Z', (byte)'i', (byte)'f'])
        {
            return null;
        }

        var start = Array.LastIndexOf(bytes, (byte)'\n', bytes.Length - 2) + 1;
        var rule = Encoding.ASCII.GetString(bytes, start, bytes.Length - 1 - start);

        // The rule lands inside a JSON string the ESP reads with strstr, so nothing that would
        // need escaping, and nothing that could look like a key (DESIGN.md §5 rule 5).
        return rule.Length is > 0 and <= EspMaxLength && rule.All(c => c is > ' ' and <= '~' and not '"' and not '\\')
            ? rule
            : null;
    }
}
