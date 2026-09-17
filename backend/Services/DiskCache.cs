using System.Text.Json;

namespace RealtouchSmartTrade.Api.Services;

// Tiny best-effort JSON file cache. Used to survive backend restarts during
// development without spending quota-limited providers' daily budgets again
// on every restart - never the source of truth, just a warm-start hint. Any
// read/write failure (missing file, corrupt JSON, locked disk) is swallowed
// and treated as "nothing cached", never surfaced as an error to the caller.
public static class DiskCache
{
    private static readonly object WriteLock = new();

    public static T? Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch
        {
            return default;
        }
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            lock (WriteLock)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(value));
            }
        }
        catch
        {
            // Best-effort only - a failed write just means the next restart
            // re-fetches instead of warm-starting from this cache.
        }
    }
}
