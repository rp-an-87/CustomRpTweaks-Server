using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace CustomRpTweaks;

/// <summary>
/// Display names of map backports ("Manimal", "Fums"), for the web pages. From MapVariants' own maps.json (the backportName of each
/// map, in its client plugin folder) and, for a backport a server mod declared, from the client plugin's GUID (com.fums.customs ->
/// Fums). Cosmetic only: anything that cannot be read just leaves the name out.
/// </summary>
internal static class VariantNames
{
    /// <summary>A name as part of a key: "Fums" -> "fums", "Halloween 2026" -> "halloween-2026".</summary>
    public static string Slug(string name) => Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    /// <summary>The data a mod offers as a version of a map through MapVariants' "source" registration, by the map it is for.</summary>
    public sealed record SourceSet(string Id, LocationBase Base);

    // MapVariants' public server API has no list of the sources, so its registry is read by reflection (its _entries and their Sources).
    // Version-bound and best effort: if the layout is not as expected there are simply no sources.
    public static Dictionary<string, List<SourceSet>> Sources(IServiceProvider services)
    {
        var result = new Dictionary<string, List<SourceSet>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MapVariants.Server.Variants.VariantRegistry", false))
                .FirstOrDefault(t => t is not null);
            var registry = type is null ? null : services.GetService(type);
            if (type?.GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(registry) is not IDictionary entries) return result;

            foreach (DictionaryEntry pair in entries)
            {
                var entry = pair.Value;
                if (pair.Key is not string map || entry is null) continue;
                if (entry.GetType().GetField("Sources")?.GetValue(entry) is not IDictionary sources || sources.Count == 0) continue;

                foreach (DictionaryEntry source in sources)
                {
                    var set = source.Value;
                    if (source.Key is string id && set?.GetType().GetProperty("Base")?.GetValue(set) is LocationBase locationBase)
                    {
                        if (!result.TryGetValue(map, out var list)) result[map] = list = new List<SourceSet>();
                        list.Add(new SourceSet(id, locationBase));
                    }
                }
            }
        }
        catch
        {
            // best effort
        }

        return result;
    }

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
