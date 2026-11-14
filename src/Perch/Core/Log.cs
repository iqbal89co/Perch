namespace Perch.Core;

/// <summary>Tiny append-only log, trimmed when it grows past 1 MB.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");
    public static void Error(Exception ex) => Write("ERROR", ex.ToString());

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = AppPaths.LogFile;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
