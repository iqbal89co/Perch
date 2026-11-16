using System.Globalization;
using Microsoft.Win32;

namespace Perch.Pages.SystemMonitor;

/// <summary>Facts that never change while Perch runs (CPU name, base clock, Windows build). Read once from the registry.</summary>
internal static class SystemInfo
{
    private const string CpuKey = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string WindowsKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private static readonly Lazy<Facts> Cached = new(Read);

    /// <summary>Base (nominal) clock in MHz, or 0 when the registry does not say.</summary>
    public static double BaseMhz => Cached.Value.BaseMhz;

    /// <summary>Raw processor name, e.g. "13th Gen Intel(R) Core(TM) i7-13700H" (padded; pass through SystemMath.CleanCpuName).</summary>
    public static string CpuName => Cached.Value.CpuName;

    /// <summary>Windows build number such as 26200, or 0 when unknown.</summary>
    public static int WindowsBuild => Cached.Value.WindowsBuild;

    private static Facts Read()
    {
        return new Facts(
            BaseMhz: ReadNumber(CpuKey, "~MHz"),
            CpuName: Registry.GetValue(CpuKey, "ProcessorNameString", null) as string ?? "",
            WindowsBuild: (int)ReadNumber(WindowsKey, "CurrentBuild"));
    }

    private static double ReadNumber(string key, string name)
    {
        try
        {
            var value = Registry.GetValue(key, name, null);
            return value switch
            {
                int i => i,
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };
        }
        catch (Exception)
        {
            return 0; // registry access denied or missing: the page falls back to what it can show
        }
    }

    private sealed record Facts(double BaseMhz, string CpuName, int WindowsBuild);
}
