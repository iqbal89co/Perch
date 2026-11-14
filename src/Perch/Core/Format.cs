namespace Perch.Core;

/// <summary>Small formatting helpers shared by pages.</summary>
public static class Format
{
    /// <summary>m:ss, or h:mm:ss past an hour. Negative values clamp to zero.</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string Clock(DateTime time, bool use24h, bool seconds = false)
    {
        if (use24h) return time.ToString(seconds ? "HH:mm:ss" : "HH:mm");
        return time.ToString(seconds ? "h:mm:ss tt" : "h:mm tt");
    }

    /// <summary>1.5 KB, 12 MB, ... using 1024 steps.</summary>
    public static string Bytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (Math.Abs(bytes) >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.#} {units[i]}";
    }
}
