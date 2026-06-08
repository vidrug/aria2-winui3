using System.Globalization;

namespace Aria2Gui.Helpers;

/// <summary>Localized size/speed/time formatting for the UI. Unit labels are resolved once
/// from resources (the UI language only changes via an app relaunch, so caching is safe).</summary>
public static class FormatUtils
{
    private static readonly string[] SizeUnits = BuildSizeUnits();
    private static readonly string SpeedSuffix = L.Get("UnitSpeedPerSecond");
    private static readonly string HourUnit = L.Get("UnitHour");
    private static readonly string MinuteUnit = L.Get("UnitMinute");
    private static readonly string SecondUnit = L.Get("UnitSecond");

    // "B,KB,MB,GB,TB" → 5 unit labels; fall back to English if the resource is malformed.
    private static string[] BuildSizeUnits()
    {
        string[] parts = L.Get("UnitsSize").Split(',');
        return parts.Length == 5 ? parts : ["B", "KB", "MB", "GB", "TB"];
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "0 " + SizeUnits[0];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string format = value >= 100 || unit == 0 ? "0" : "0.#";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + SizeUnits[unit];
    }

    public static string FormatSpeed(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? "0 " + SizeUnits[0] + SpeedSuffix : FormatSize(bytesPerSecond) + SpeedSuffix;

    /// <summary>Estimated time remaining, or "—" when unknown.</summary>
    public static string FormatEta(long totalLength, long completedLength, long downloadSpeed)
    {
        if (downloadSpeed <= 0 || totalLength <= 0 || completedLength >= totalLength)
            return "—";
        double seconds = (totalLength - completedLength) / (double)downloadSpeed;
        if (seconds > 30 * 24 * 3600)
            return "∞";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} {HourUnit} {ts.Minutes} {MinuteUnit}";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes} {MinuteUnit} {ts.Seconds} {SecondUnit}";
        return $"{ts.Seconds} {SecondUnit}";
    }
}
