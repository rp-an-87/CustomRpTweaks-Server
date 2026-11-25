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

// Map backports (served beside the shipped map by the MapVariants mod) get their own extract settings. The extract tweaks of
// TweaksApplier are written to the shipped maps' data when the server starts, before the backports load, so a backport raid
// would otherwise ignore them. A backport is "<map>@backport" in config.json (extracts.chances.locations, extracts.coop.locations).
//
// MapVariants is reached by reflection (no reference to it). Which variant is served is asked at raid time and the settings are
// applied to the copy of the map data the server makes for that raid (LocationLifecycleService.GenerateLocationAndLoot), not to the
// stored data: MapVariants copies configuration changes made to a backport onto the shipped map, which would mix the two up.
// A setting a backport does not have inherits the shipped map's.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 5)]
public class BackportVariants(IServiceProvider services, LocationTable locationTable, TemplateTable templateTable, ISptLogger<BackportVariants> logger, ModHelper modHelper) : IOnLoad
{
    internal static BackportVariants? Instance;

    // BTR maps SPT itself ships (Woods, Streets, ...), set by TweaksApplier before any backport loads
    internal static HashSet<string> VanillaBtrMaps = new(StringComparer.OrdinalIgnoreCase);

    // "<map>@backport" of the backports found at this start (the web page only shows settings of installed backports)
    internal static HashSet<string> Detected = new(StringComparer.OrdinalIgnoreCase);

    // map -> who made its backport ("Manimal"), for the web page
    internal static Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    // "lighthouse@backport" -> "lighthouse (Manimal)"; other keys unchanged
    internal static string Label(string key)
    {
        if (!key.EndsWith("@backport", StringComparison.OrdinalIgnoreCase)) return key;
        var map = key[..^"@backport".Length];
        return $"{map} ({(Names.TryGetValue(map, out var name) ? name : "backport")})";
    }

    // false for a "<map>@backport" setting of a backport that is not installed, and for a BTR map only a backport brings
    internal static bool IsShown(string key) => !key.EndsWith("@backport", StringComparison.OrdinalIgnoreCase) || Detected.Contains(key);

    internal static bool BtrMapShown(string map) => VanillaBtrMaps.Count == 0 || VanillaBtrMaps.Contains(map) || Detected.Contains(map.ToLowerInvariant() + "@backport");

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
            logger.Warning("[CustomRpTweaks] MapVariants is installed but its Providers service was not found; backport extract settings are off.");
            return Task.CompletedTask;
        }

        try
        {
            var detected = DetectBackports();
            Detected = new HashSet<string>(detected, StringComparer.OrdinalIgnoreCase);
            Names = VariantNames.Load(services);
            if (detected.Count > 0)
            {
                Seed(detected);
            }

            var config = modHelper.GetJsonDataFromFile<TweaksConfig>(_modPath, "config.json");
            _extracts = config.Extracts;
            _btr = config.Btr;
            Instance = this;

            var target = AccessTools.Method(typeof(LocationLifecycleService), "GenerateLocationAndLoot");
            if (target is null)
            {
                logger.Warning("[CustomRpTweaks] LocationLifecycleService.GenerateLocationAndLoot was not found; backport extract settings are off.");
                return Task.CompletedTask;
            }

            new Harmony("com.rpan87.customrptweaks.backportvariants").Patch(
                target,
                postfix: new HarmonyMethod(typeof(BackportVariantPatches), nameof(BackportVariantPatches.Postfix)));
            logger.Info($"[CustomRpTweaks] Backport extract settings: {(detected.Count > 0 ? string.Join(", ", detected) : "no backport found")}");
        }
        catch (Exception ex)
        {
            logger.Error("[CustomRpTweaks] Backport extract settings failed to start; backports use the maps' own extracts.", ex);
        }

        return Task.CompletedTask;
    }

    // Maps whose live data is not the shipped data MapVariants captured: a backport replaced it
    private List<string> DetectBackports()
    {
        var found = new List<string>();
        foreach (var (key, location) in locationTable.GetDictionary())
        {
            if (location?.Base is null) continue;

            var shipped = _shipped!.Invoke(_providers, [key]) as Location;
            if (shipped?.Base is not null && !ReferenceEquals(shipped.Base, location.Base))
            {
                found.Add(key.ToLowerInvariant() + "@backport");
            }
        }

        return found;
    }

    // Adds the detected backports to config.json, with the shipped map's values, so they can be edited (once; never overwrites)
    private void Seed(List<string> backports)
    {
        var path = Path.Combine(_modPath, "config.json");
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject();
        var extracts = root?["extracts"]?.AsObject();
        if (extracts is null) return;

        var chances = extracts["chances"]?["locations"]?.AsObject();
        var coop = extracts["coop"]?.AsObject();
        var changed = false;

        foreach (var key in backports)
        {
            var map = key[..^"@backport".Length];

            if (chances is not null && !chances.ContainsKey(key) && chances[map] is JsonValue shippedChance)
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

        File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmm}-pre-backports", overwrite: false);
        File.WriteAllText(path, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        logger.Info($"[CustomRpTweaks] Backport extract settings added to config.json: {string.Join(", ", backports)}");
    }

    // The key of the data set a raid on this map is served from, or null for the shipped map
    internal string? ServedKey(string locationName)
    {
        try
        {
            var variant = _installedVariant!.Invoke(_providers, [locationName]) as string;
            if (string.IsNullOrEmpty(variant) || variant == "original") return null;
            return $"{locationName.ToLowerInvariant()}@{variant}";
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

        // Extract chances: the variant's own value, else the shipped map's
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

        // Paid co-op extracts: the variant's own entry, else the global one
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
