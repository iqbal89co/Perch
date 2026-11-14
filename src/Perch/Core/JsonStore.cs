using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch.Core;

/// <summary>
/// Persists small JSON documents under AppPaths.Data. Writes are atomic (temp file + move) and the
/// previous version is kept as name.bak.json. A corrupt file is set aside and replaced by defaults.
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private static readonly object Gate = new();

    public static string PathFor(string name) => Path.Combine(AppPaths.Data, name + ".json");

    public static T Load<T>(string name) where T : new()
    {
        var file = PathFor(name);
        lock (Gate)
        {
            try
            {
                if (!File.Exists(file)) return new T();
                var json = File.ReadAllText(file);
                return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
            }
            catch (Exception ex)
            {
                Log.Warn($"store: {name} unreadable ({ex.Message}); using defaults");
                try { File.Copy(file, file + ".corrupt", overwrite: true); } catch { /* ignore */ }
                return new T();
            }
        }
    }

    public static void Save<T>(string name, T value)
    {
        var file = PathFor(name);
        lock (Gate)
        {
            try
            {
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
                if (File.Exists(file))
                    File.Copy(file, Path.Combine(AppPaths.Data, name + ".bak.json"), overwrite: true);
                File.Move(tmp, file, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error($"store: could not save {name}", ex);
            }
        }
    }
}
