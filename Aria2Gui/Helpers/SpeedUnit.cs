using System.Globalization;

namespace Aria2Gui.Helpers;

/// <summary>
/// Shared model for the "speed limit with selectable unit" feature: a numeric value plus a unit
/// symbol that together describe a byte/second cap. aria2's <c>max-*-limit</c> options take a plain
/// byte-count string (e.g. "262144000"), so <see cref="ToBytes"/> produces what we store/send and
/// <see cref="FromBytes"/> turns a stored byte count back into a displayable value in a chosen unit.
/// Binary multipliers; bit units are byte/8 (so "Mb" = megabit/s).
/// </summary>
public static class SpeedUnit
{
    /// <summary>Default unit shown when no better one applies (e.g. a 0/unlimited limit).</summary>
    public const string Default = "MB";

    /// <summary>Unit symbols in the order the flyout and settings combos list them (byte and bit
    /// units interleaved, not by magnitude). <see cref="BestUnit"/> sorts by multiplier itself.</summary>
    public static readonly string[] Symbols = ["B", "KB", "Kb", "MB", "Mb"];

    /// <summary>Bytes-per-unit for each symbol. Binary (KB=1024); bit units are bytes/8.</summary>
    private static double Multiplier(string unit) => unit switch
    {
        "B" => 1,
        "KB" => 1024,
        "Kb" => 1024d / 8,        // 128
        "MB" => 1024d * 1024,     // 1048576
        "Mb" => 1024d * 1024 / 8, // 131072
        _ => 1024d * 1024,        // unknown → MB
    };

    /// <summary>True for one of the five supported unit symbols.</summary>
    public static bool IsValid(string? unit) => unit is not null && Array.IndexOf(Symbols, unit) >= 0;

    /// <summary>Coerces an unknown/empty unit to <see cref="Default"/>.</summary>
    public static string Sanitize(string? unit) => IsValid(unit) ? unit! : Default;

    /// <summary>Converts a value in the given unit to a whole byte count (rounded). 0 (or below) → 0.</summary>
    public static long ToBytes(double value, string unit)
    {
        if (double.IsNaN(value) || value <= 0)
            return 0;
        return (long)Math.Round(value * Multiplier(unit), MidpointRounding.AwayFromZero);
    }

    /// <summary>Converts a byte count back to a value expressed in <paramref name="preferredUnit"/>
    /// (value = bytes / multiplier). Returns 0 for a non-positive byte count.</summary>
    public static (double value, string unit) FromBytes(long bytes, string preferredUnit)
    {
        string unit = Sanitize(preferredUnit);
        if (bytes <= 0)
            return (0, unit);
        // Round for display so the NumberBox shows e.g. "4.77 MB", not "4.7683715820 MB" (B25).
        return (Math.Round(bytes / Multiplier(unit), 2), unit);
    }

    /// <summary>Picks the largest unit that still yields a value ≥ 1 for the given byte count,
    /// so a stored limit displays with a sensible unit; falls back to "B", and to
    /// <see cref="Default"/> when the count is 0 (no limit).</summary>
    public static string BestUnit(long bytes)
    {
        if (bytes <= 0)
            return Default;
        // "Largest unit" = largest multiplier (MB > Mb > KB > Kb > B), independent of the display
        // order in Symbols; pick the first whose value stays ≥ 1, else fall back to bytes.
        string best = "B";
        double bestMult = 0;
        foreach (string u in Symbols)
        {
            double m = Multiplier(u);
            if (bytes / m >= 1 && m > bestMult)
            {
                best = u;
                bestMult = m;
            }
        }
        return best;
    }

    /// <summary>Parses a stored limit string into bytes. Accepts a plain byte count ("262144000"),
    /// "0"/empty (no limit), and legacy aria2 grammar with a K/M suffix ("10240K", "5M").</summary>
    public static long ParseStoredBytes(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return 0;
        string t = stored.Trim();
        double mult = 1;
        char last = char.ToUpperInvariant(t[^1]);
        if (last == 'K') { mult = 1024; t = t[..^1]; }
        else if (last == 'M') { mult = 1024 * 1024; t = t[..^1]; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0
            ? (long)Math.Round(n * mult, MidpointRounding.AwayFromZero)
            : 0;
    }
}
