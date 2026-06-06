using System.Globalization;

namespace Aria2Gui.Helpers;

/// <summary>Russian-language size/speed/time formatting for the UI.</summary>
public static class FormatUtils
{
    private static readonly string[] SizeUnits = ["Б", "КБ", "МБ", "ГБ", "ТБ"];

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "0 Б";
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
        bytesPerSecond <= 0 ? "0 Б/с" : FormatSize(bytesPerSecond) + "/с";

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
            return $"{(int)ts.TotalHours} ч {ts.Minutes} мин";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes} мин {ts.Seconds} с";
        return $"{ts.Seconds} с";
    }
}
