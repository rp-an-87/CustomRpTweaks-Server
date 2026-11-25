using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.InRaid;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CustomRpTweaks;

// Versions of a map that the MapVariants mod serves beside the shipped one (a backport, and others other mods offer) get their own
// extract settings. The extract tweaks of TweaksApplier are written to the shipped maps' data when the server starts, before the
// backports load, so a raid on another version would otherwise ignore them. A version is "<map>@<name>" in config.json
// (extracts.chances.locations, extracts.coop.locations), the name built at start from its author ("manimal", "fums") or, for a version
// another mod offers, its id ("classic"), so a map can have several.
//
// MapVariants is reached by reflection (no reference to it). Which variant is served is asked at raid time and the settings are
// applied to the copy of the map data the server makes for that raid (LocationLifecycleService.GenerateLocationAndLoot), not to the
// stored data: MapVariants copies configuration changes made to a backport onto the shipped map, which would mix the two up.
// A setting a version does not have inherits the shipped map's.
//
// Late in PostLoad, just before MapVariants' own discovery (PostLoad + 500000): a backport such as Fums' Customs installs its data during
// PostLoad, and the other mods register their versions then, so looking earlier would miss them.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 499000)]
public class BackportVariants(IServiceProvider services, LocationTable locationTable, TemplateTable templateTable, ISptLogger<BackportVariants> logger, ModHelper modHelper) : IOnLoad
{
    internal static BackportVariants? Instance;

    // BTR maps SPT itself ships (Woods, Streets, ...), set by TweaksApplier before any backport loads
    internal static HashSet<string> VanillaBtrMaps = new(StringComparer.OrdinalIgnoreCase);

    // "<map>@<name>" of the versions found at this start (the web page only shows settings of installed ones) and who made each
    internal static HashSet<string> Detected = new(StringComparer.OrdinalIgnoreCase);
    internal static Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    // "lighthouse@manimal" -> "lighthouse (Manimal)"; a key without a version is unchanged
    internal static string Label(string key)
    {
        var at = key.IndexOf('@');
        if (at < 0) return key;
        return $"{key[..at]} ({(Names.TryGetValue(key, out var name) ? name : "variant")})";
    }

    // false for a "<map>@<name>" setting of a version that is not installed, and for a BTR map only a backport brings
    internal static bool IsShown(string key) => !key.Contains('@') || Detected.Contains(key);

    internal static bool BtrMapShown(string map) => VanillaBtrMaps.Count == 0 || VanillaBtrMaps.Contains(map) || Detected.Any(key => key.StartsWith(map + "@", StringComparison.OrdinalIgnoreCase));

    // the label of a BTR map a backport brings
    internal static string BtrLabel(string map)
        => Detected.FirstOrDefault(key => key.StartsWith(map + "@", StringComparison.OrdinalIgnoreCase)) is { } key ? Label(key) : map;

    private sealed record Variant(string Map, string VariantId, string Key, string Name);

    // (map, MapVariants' id of the version: "backport" or the id of a source) -> key
    private readonly Dictionary<(string, string), string> _keys = new();

    // chance a backport-only BTR map had before it was switched off for a shipped-map raid
    private readonly Dictionary<string, double> _btrChance = new(StringComparer.OrdinalIgnoreCase);
    private BtrConfig? _btr;

    private const string EuroId = "569668774bdc2da2298b4568";

    private object? _providers;
    private MethodInfo? _installedVariant;
    private MethodInfo? _shipped;
    private ExtractsConfig? _extracts;
    private string _modPath = "";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        _modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("MapVariants.Server.Variants.Providers", false))
            .FirstOrDefault(t => t is not null);
        if (type is null)
        {
            return Task.CompletedTask;
        }

        _installedVariant = type.GetMethod("InstalledVariant", [typeof(string)]);
        _shipped = type.GetMethod("Shipped", [typeof(string)]);
        _providers = services.GetService(type);
        if (_providers is null || _installedVariant is null || _shipped is null)
        {
            logger.Warning("[CustomRpTweaks] MapVariants is installed but its Providers service was not found; extract settings per map version are off.");
            return Task.CompletedTask;
        }

        try
        {
            var found = Detect();
            Detected = new HashSet<string>(found.Select(v => v.Key), StringComparer.OrdinalIgnoreCase);
            Names = found.ToDictionary(v => v.Key, v => v.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var variant in found) _keys[(variant.Map, variant.VariantId)] = variant.Key;
            if (found.Count > 0)
            {
                Seed(found);
            }

            var config = modHelper.GetJsonDataFromFile<TweaksConfig>(_modPath, "config.json");
            _extracts = config.Extracts;
            _btr = config.Btr;
            Instance = this;

            var target = AccessTools.Method(typeof(LocationLifecycleService), "GenerateLocationAndLoot");
            if (target is null)
            {
                logger.Warning("[CustomRpTweaks] LocationLifecycleService.GenerateLocationAndLoot was not found; extract settings per map version are off.");
                return Task.CompletedTask;
            }

            new Harmony("com.rpan87.customrptweaks.backportvariants").Patch(
                target,
                postfix: new HarmonyMethod(typeof(BackportVariantPatches), nameof(BackportVariantPatches.Postfix)));
            logger.Info($"[CustomRpTweaks] Extract settings per map version: {(found.Count > 0 ? string.Join(", ", found.Select(v => v.Key)) : "no other version found")}");
        }
        catch (Exception ex)
        {
            logger.Error("[CustomRpTweaks] Extract settings per map version failed to start; the other versions use the maps' own extracts.", ex);
        }

        return Task.CompletedTask;
    }

    // The versions of each map besides the shipped one: a backport (the live data is not the shipped data MapVariants captured) and
    // the versions other mods offer as sources
    private List<Variant> Detect()
    {
        var found = new List<Variant>();
        var backportNames = VariantNames.Load(services);
        var sources = VariantNames.Sources(services);

        foreach (var (mapKey, location) in locationTable.GetDictionary())
        {
            if (location?.Base is null) continue;

            var map = mapKey.ToLowerInvariant();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var shipped = _shipped!.Invoke(_providers, [mapKey]) as Location;
            if (shipped?.Base is not null && !ReferenceEquals(shipped.Base, location.Base))
            {
                var name = backportNames.GetValueOrDefault(map);
                var slug = string.IsNullOrEmpty(name) ? "" : VariantNames.Slug(name);
                if (slug.Length == 0) slug = "backport";
                used.Add(slug);
                found.Add(new Variant(map, "backport", $"{map}@{slug}", name ?? "Backport"));
            }

            if (sources.TryGetValue(mapKey, out var sets) || sources.TryGetValue(map, out sets))
            {
                foreach (var set in sets)
                {
                    var slug = VariantNames.Slug(set.Id);
                    if (slug.Length == 0) slug = "source";
                    var unique = slug;
                    for (var n = 2; !used.Add(unique); n++) unique = $"{slug}-{n}";
                    found.Add(new Variant(map, set.Id, $"{map}@{unique}", char.ToUpperInvariant(set.Id[0]) + set.Id[1..]));
                }
            }
        }

        return found;
    }

    // Adds the versions found to config.json, with the shipped map's values, so they can be edited (once; never overwrites)
    private void Seed(List<Variant> variants)
    {
        var path = Path.Combine(_modPath, "config.json");
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject();
        var extracts = root?["extracts"]?.AsObject();
        if (extracts is null) return;

        var chances = extracts["chances"]?["locations"]?.AsObject();
        var coop = extracts["coop"]?.AsObject();
        var changed = false;

        foreach (var variant in variants)
        {
            var key = variant.Key;

            if (chances is not null && !chances.ContainsKey(key) && chances[variant.Map] is JsonValue shippedChance)
            {
                chances[key] = shippedChance.DeepClone();
                changed = true;
            }

            if (coop is not null)
            {
                var locations = coop["locations"]?.AsObject();
                if (locations is null)
                {
                    locations = new JsonObject();
                    coop["locations"] = locations;
                }

                if (!locations.ContainsKey(key))
                {
                    locations[key] = new JsonObject
                    {
                        ["enabled"] = coop["enabled"]?.DeepClone() ?? true,
                        ["amount"] = coop["amount"]?.DeepClone() ?? 1000
                    };
                    changed = true;
                }
            }
        }

        if (!changed) return;

        File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmm}-pre-variants", overwrite: false);
        File.WriteAllText(path, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        logger.Info($"[CustomRpTweaks] Extract settings added to config.json: {string.Join(", ", variants.Select(v => v.Key))}");
    }

    // The key of the data set a raid on this map is served from, or null for the shipped map
    internal string? ServedKey(string locationName)
    {
        try
        {
            var variant = _installedVariant!.Invoke(_providers, [locationName]) as string;
            if (string.IsNullOrEmpty(variant) || variant == "original") return null;

            var map = locationName.ToLowerInvariant();
            return _keys.TryGetValue((map, variant), out var key) ? key : $"{map}@{variant}";
        }
        catch
        {
            return null;
        }
    }

    // A BTR map that only a backport brings (Manimal's Lighthouse) must not spawn a BTR on the shipped map, which has none of its
    // scenery: its chance is 0 while the shipped map is served and its own again while the backport is. Settings are sent with the
    // raid, after this runs.
    private void ApplyBtr(string locationName, string? servedKey)
    {
        var maps = templateTable.LocationServices.BtrServerSettings.ServerMapBTRSettings;
        foreach (var (btrName, settings) in maps)
        {
            if (VanillaBtrMaps.Contains(btrName) || !btrName.Equals(locationName, StringComparison.OrdinalIgnoreCase)) continue;

            if (servedKey is null)
            {
                if (settings.ChanceSpawn != 0)
                {
                    _btrChance[btrName] = settings.ChanceSpawn;
                    settings.ChanceSpawn = 0;
                    logger.Info($"[CustomRpTweaks] BTR {btrName}: off, the shipped map is served (the BTR only exists on the backport)");
                }
            }
            else if (_btrChance.TryGetValue(btrName, out var chance))
            {
                settings.ChanceSpawn = chance;
                _btrChance.Remove(btrName);
                logger.Info($"[CustomRpTweaks] BTR {btrName}: chance {chance}, the backport is served");
            }
        }
    }

    internal void Apply(string locationName, LocationBase? raid)
    {
        var key = ServedKey(locationName);
        ApplyBtr(locationName, key);

        if (raid?.Exits is null || _extracts is null) return;
        if (key is null) return;

        var map = locationName.ToLowerInvariant();

        // Extract chances: the version's own value, else the shipped map's
        if (_extracts.Chances is { Enabled: true } chances
            && (chances.Locations.TryGetValue(key, out var chance) || chances.Locations.TryGetValue(map, out chance)))
        {
            foreach (var exit in raid.Exits.Where(e => e.Chance is not null and not 0))
            {
                if (exit.Chance != chance)
                {
                    logger.Info($"[CustomRpTweaks] {key} exit '{exit.Name}' chance {exit.Chance} -> {chance}");
                    exit.Chance = chance;
                }
            }
        }

        // Paid co-op extracts: the version's own entry, else the global one
        var coopEnabled = _extracts.Coop?.Enabled ?? false;
        var coopAmount = _extracts.Coop?.Amount ?? 1000;
        if (_extracts.Coop?.Locations.TryGetValue(key, out var own) == true)
        {
            coopEnabled = own.Enabled;
            coopAmount = own.Amount;
        }

        if (coopEnabled)
        {
            foreach (var exit in raid.Exits.Where(e => e.PassageRequirement == RequirementState.ScavCooperation))
            {
                exit.PassageRequirement = RequirementState.TransferItem;
                exit.RequirementTip = "EXFIL_Item";
                exit.Id = EuroId;
                exit.Count = coopAmount;
                logger.Info($"[CustomRpTweaks] {key} coop exit '{exit.Name}' now costs {coopAmount} euros");
            }
        }
    }
}

public static class BackportVariantPatches
{
    // name: the location the raid is for; __result: the copy of its data made for this raid
    public static void Postfix(string name, LocationBase __result)
    {
        BackportVariants.Instance?.Apply(name, __result);
    }
}
