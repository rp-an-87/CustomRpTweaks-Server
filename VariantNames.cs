using System.Reflection;
using System.Text.Json;

namespace CustomRpTweaks;

/// <summary>
/// Display names of map backports ("Manimal", "Fums"), for the web pages. From MapVariants' own maps.json (the backportName of each
/// map, in its client plugin folder) and, for a backport a server mod declared, from the client plugin's GUID (com.fums.customs ->
/// Fums). Cosmetic only: anything that cannot be read just leaves the name out.
/// </summary>
internal static class VariantNames
{
    public static Dictionary<string, string> Load(IServiceProvider services)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BepInEx"))) dir = dir.Parent;
            var plugins = dir is null ? null : Path.Combine(dir.FullName, "BepInEx", "plugins");
            if (plugins is not null && Directory.Exists(plugins))
            {
                foreach (var folder in Directory.EnumerateDirectories(plugins, "*MapVariants*"))
                {
                    var file = Path.Combine(folder, "maps.json");
                    if (!File.Exists(file)) continue;

                    using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (!doc.RootElement.TryGetProperty("maps", out var maps)) continue;
                    foreach (var map in maps.EnumerateArray())
                    {
                        var name = map.TryGetProperty("backportName", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name) || !map.TryGetProperty("locationIds", out var ids)) continue;
                        foreach (var id in ids.EnumerateArray())
                        {
                            if (id.GetString() is { } location) names[location] = name;
                        }
                    }
                }
            }
        }
        catch
        {
            // cosmetic
        }

        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MapVariants.Server.Variants.VariantRegistry", false))
                .FirstOrDefault(t => t is not null);
            var registry = type is null ? null : services.GetService(type);
            if (type?.GetField("_backportPlugins", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(registry) is IDictionary<string, string> plugins)
            {
                foreach (var (location, guid) in plugins)
                {
                    var parts = guid.Split('.');
                    if (parts.Length > 1 && parts[1].Length > 0) names.TryAdd(location, char.ToUpperInvariant(parts[1][0]) + parts[1][1..]);
                }
            }
        }
        catch
        {
            // cosmetic
        }

        return names;
    }
}
