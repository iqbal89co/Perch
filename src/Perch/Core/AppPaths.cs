namespace Perch.Core;

/// <summary>Where Perch keeps its files. PERCH_DATA_DIR overrides the root (used by tests).</summary>
public static class AppPaths
{
    private static string? _root;

    public static string Root
    {
        get
        {
            if (_root != null) return _root;
            var overrideDir = Environment.GetEnvironmentVariable("PERCH_DATA_DIR");
            _root = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Perch");
            Directory.CreateDirectory(_root);
            return _root;
        }
    }

    /// <summary>For tests: point the store at a private directory.</summary>
    public static void OverrideRoot(string dir)
    {
        Directory.CreateDirectory(dir);
        _root = dir;
    }

    public static string Data => Ensure(Path.Combine(Root, "data"));
    public static string Cache => Ensure(Path.Combine(Root, "cache"));
    public static string Shelf => Ensure(Path.Combine(Root, "shelf"));
    public static string LogFile => Path.Combine(Root, "perch.log");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
