using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Web.Models.Configs;
using SPTarkov.Server.Web.Services;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.rpan87.customrptweaks";
    public string Name { get; init; } = "CustomRpTweaks";
    public string Author { get; init; } = "rp-an-87";
    public List<string>? Contributors { get; init; } = ["Based on LootValuePlus by rp-an-87"];
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

// Applies the tweaks from config.json once the database is loaded.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 1)]
public class TweaksApplier(LocationTable locationTable, TemplateTable templateTable, TraderConfig traderConfig, ISptLogger<TweaksApplier> logger, ModHelper modHelper) : IOnLoad
{
    private const string EuroId = "569668774bdc2da2298b4568";
    private const string ItemCaseId = "59fb042886f7746c5005a7b2";
    private const string ThiccItemCaseId = "5c0a840b86f7742ffa4f2482";
    private static readonly string[] SecureContainerNodes = ["5448bf274bdc2dfc2f8b456a"];
    private static readonly string[] AmmoBoxNodes = ["543be5cb4bdc2deb348b4568"];
    private static readonly string[] SpecialNodes =
        ["62e9103049c018f425059f38", "54009119af1c881c07000029", "65ddcc7aef36f6413d0829b9", "6672e40ebb23210ae87d39eb"];

    private const int MaxSpecialSlots = 6;
    // pockets template, whether it is the Unheard Edition one, and the fixed _id of its SpecialSlot4, 5 and 6
    private static readonly (string Id, bool Unheard, string[] SlotIds)[] PocketsTemplates =
    [
        ("627a4e6b255f7527fb05a0f6", false, ["281451b393acbc1cabadc05f", "0bd4dc029dcc64b768cbfd19", "6c8dfaff68d5e3a726708ff6"]),
        ("65e080be269cbd5c5005e529", true, ["8db5e273cbf171000d279136", "5d1f0a7b9e3c4a2864b0e1c7", "3a9c7e14d2b85f60a1c4e9b2"]),
    ];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json");

        ApplyExtracts(config.Extracts);
        ApplyBtr(config.Btr);
        ApplyContainers(config.Containers);
        ApplyPockets(config.Pockets);
        ApplyTraderRestock(config.TraderRestock);
        return Task.CompletedTask;
    }

    // location database name (as used in config.json) -> location
    private Dictionary<string, SPTarkov.Server.Core.Models.Eft.Common.Location> AllLocations()
    {
        var all = new Dictionary<string, SPTarkov.Server.Core.Models.Eft.Common.Location?>
        {
            ["bigmap"] = locationTable.Bigmap,
            ["factory4_day"] = locationTable.Factory4Day,
            ["factory4_night"] = locationTable.Factory4Night,
            ["interchange"] = locationTable.Interchange,
            ["laboratory"] = locationTable.Laboratory,
            ["lighthouse"] = locationTable.Lighthouse,
            ["rezervbase"] = locationTable.RezervBase,
            ["shoreline"] = locationTable.Shoreline,
            ["tarkovstreets"] = locationTable.TarkovStreets,
            ["woods"] = locationTable.Woods,
            ["labyrinth"] = locationTable.Labyrinth,
            ["sandbox"] = locationTable.Sandbox,
        };
        return all.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
    }

    private void ApplyExtracts(ExtractsConfig? cfg)
    {
        if (cfg is null)
        {
            return;
        }

        var locations = AllLocations();

        if (cfg.Chances is { Enabled: true })
        {
            foreach (var (name, chance) in cfg.Chances.Locations)
            {
                if (!locations.TryGetValue(name, out var location) || location.Base?.Exits is null)
                {
                    continue;
                }

                foreach (var exit in location.Base.Exits.Where(e => e.Chance is not null and not 0))
                {
                    if (exit.Chance != chance)
                    {
                        logger.Info($"[CustomRpTweaks] {name} exit '{exit.Name}' chance {exit.Chance} -> {chance}");
                        exit.Chance = chance;
                    }
                }
            }
        }

        if (cfg.Coop is { Enabled: true })
        {
            foreach (var (name, location) in locations)
            {
                if (location.Base?.Exits is null)
                {
                    continue;
                }

                foreach (var exit in location.Base.Exits.Where(e => e.PassageRequirement == RequirementState.ScavCooperation))
                {
                    exit.PassageRequirement = RequirementState.TransferItem;
                    exit.RequirementTip = "EXFIL_Item";
                    exit.Id = EuroId;
                    exit.Count = cfg.Coop.Amount;
                    logger.Info($"[CustomRpTweaks] {name} coop exit '{exit.Name}' now costs {exit.Count} euros");
                }
            }
        }
    }

    private void ApplyBtr(BtrConfig? cfg)
    {
        if (cfg is not { Enabled: true })
        {
            return;
        }

        var maps = templateTable.LocationServices.BtrServerSettings.ServerMapBTRSettings;
        foreach (var (name, wanted) in cfg.Locations)
        {
            if (!maps.TryGetValue(name, out var map))
            {
                logger.Warning($"[CustomRpTweaks] BTR map '{name}' not found, skipped");
                continue;
            }

            map.ChanceSpawn = wanted.Chance;
            // Vector3 is an init-only struct: keep Z, replace X (min) and Y (max)
            map.PauseDurationRange = new SPTarkov.Server.Core.Models.Eft.Common.Vector3(wanted.StopDuration.Min, wanted.StopDuration.Max, map.PauseDurationRange.Z);
            map.SpawnPeriod = new SPTarkov.Server.Core.Models.Eft.Common.Vector3(wanted.SpawnTime.Min, wanted.SpawnTime.Max, map.SpawnPeriod.Z);
            logger.Info($"[CustomRpTweaks] BTR {name}: chance {wanted.Chance}, stop {wanted.StopDuration.Min}-{wanted.StopDuration.Max}s, spawn {wanted.SpawnTime.Min}-{wanted.SpawnTime.Max}s");
        }
    }

    private void ApplyContainers(ContainersConfig? cfg)
    {
        if (cfg?.AllowItems is not { } allow)
        {
            return;
        }

        var nodes = new List<string>();
        if (allow.Items?.SecureContainers == true) nodes.AddRange(SecureContainerNodes);
        if (allow.Items?.AmmoBoxes == true) nodes.AddRange(AmmoBoxNodes);
        if (allow.Items?.Specials == true) nodes.AddRange(SpecialNodes);

        if (allow.ThiccItemCase) AddToFilter(ThiccItemCaseId, "THICC Item Case", nodes);
        if (allow.ItemCase) AddToFilter(ItemCaseId, "Item Case", nodes);
    }

    // Special slots are plain data: the client builds one view per slot named SpecialSlotN on the pockets item.
    // We clone SpecialSlot3 (same item filters) into SpecialSlot4.. with fixed ids so they stay stable between restarts.
    private void ApplyPockets(PocketsConfig? cfg)
    {
        if (cfg is not { Enabled: true })
        {
            return;
        }

        foreach (var (pocketsId, unheard, slotIds) in PocketsTemplates)
        {
            var wanted = Math.Clamp(unheard ? cfg.SpecialSlotsUnheard : cfg.SpecialSlots, 3, MaxSpecialSlots);
            if (!templateTable.Items.TryGetValue(pocketsId, out var pockets) || pockets.Properties?.Slots is null)
            {
                logger.Warning($"[CustomRpTweaks] Pockets template {pocketsId} not found, skipped");
                continue;
            }

            var slots = pockets.Properties.Slots.ToList();
            var template = slots.FirstOrDefault(sl => sl.Name == "SpecialSlot3");
            if (template is null)
            {
                logger.Warning($"[CustomRpTweaks] Pockets template {pocketsId} has no SpecialSlot3, skipped");
                continue;
            }

            for (var n = 4; n <= wanted; n++)
            {
                var name = $"SpecialSlot{n}";
                if (slots.Any(sl => sl.Name == name))
                {
                    continue;
                }

                slots.Add(template with { Name = name, Id = new MongoId(slotIds[n - 4]) });
            }

            pockets.Properties.Slots = slots;
            logger.Info($"[CustomRpTweaks] Pockets {pocketsId} now has {slots.Count(sl => sl.Name?.StartsWith("SpecialSlot") == true)} special slots");
        }
    }

    // Caps each trader's restock interval, never lengthens it: a trader that already restocks faster keeps its own timer.
    private void ApplyTraderRestock(TraderRestockConfig? cfg)
    {
        if (cfg is not { Enabled: true })
        {
            return;
        }

        var excluded = cfg.ExcludedTraders.Select(id => new MongoId(id)).ToHashSet();
        foreach (var trader in traderConfig.UpdateTime.Where(t => !excluded.Contains(t.TraderId)))
        {
            var (oldMin, oldMax) = (trader.Seconds.Min, trader.Seconds.Max);
            trader.Seconds.Min = Math.Min(oldMin, cfg.MinSeconds);
            trader.Seconds.Max = Math.Min(oldMax, cfg.MaxSeconds);
            if (trader.Seconds.Min != oldMin || trader.Seconds.Max != oldMax)
            {
                logger.Info($"[CustomRpTweaks] Trader {trader.TraderId} restock {oldMin}-{oldMax}s -> {trader.Seconds.Min}-{trader.Seconds.Max}s");
            }
        }

        // Traders without their own entry use the default, which can also be too long
        traderConfig.UpdateTimeDefault = Math.Min(traderConfig.UpdateTimeDefault, cfg.MaxSeconds);
    }

    private void AddToFilter(string itemId, string label, List<string> nodes)
    {
        var items = templateTable.Items;
        if (!items.TryGetValue(itemId, out var item))
        {
            logger.Warning($"[CustomRpTweaks] {label} ({itemId}) not found, skipped");
            return;
        }

        var filter = item.Properties?.Grids?.FirstOrDefault()?.Properties?.Filters?.FirstOrDefault()?.Filter;
        if (filter is null)
        {
            logger.Warning($"[CustomRpTweaks] {label} has no grid filter, skipped");
            return;
        }

        foreach (var node in nodes.Where(n => !filter.Contains(n)))
        {
            filter.Add(node);
        }

        logger.Info($"[CustomRpTweaks] {label} now accepts {nodes.Count} extra item groups");
    }
}

// Lets the web config editor (/configs) list and edit this mod's config.json
[Injectable(InjectionType.Singleton)]
public class TweaksConfigProvider(ModHelper modHelper) : IConfigEditorConfigProvider
{
    public IEnumerable<ConfigEditorConfigRegistration> GetConfigs()
    {
        var meta = new ModMetadata();
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        yield return ConfigEditorConfigRegistration.Create(
            meta.ModGuid,
            meta.Name,
            modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json"),
            Path.Combine(modPath, "config.json"));
    }
}

public class TweaksConfig
{
    [JsonPropertyName("extracts")] public ExtractsConfig? Extracts { get; set; }
    [JsonPropertyName("btr")] public BtrConfig? Btr { get; set; }
    [JsonPropertyName("containers")] public ContainersConfig? Containers { get; set; }
    [JsonPropertyName("pockets")] public PocketsConfig? Pockets { get; set; }
    [JsonPropertyName("traderRestock")] public TraderRestockConfig? TraderRestock { get; set; }
}

public class TraderRestockConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // restock interval limits in seconds (2400 = 40 min, 5400 = 1.5 h)
    [JsonPropertyName("minSeconds")] public int MinSeconds { get; set; } = 2400;
    [JsonPropertyName("maxSeconds")] public int MaxSeconds { get; set; } = 5400;
    // trader ids left untouched: Fence and Ref
    [JsonPropertyName("excludedTraders")] public List<string> ExcludedTraders { get; set; } = ["579dc571d53a0658a154fbec", "6617beeaa9cfa777ca915b7c"];
}

public class PocketsConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // total special slots (3 is vanilla, max 6) for the standard pockets and for the Unheard Edition ones
    [JsonPropertyName("specialSlots")] public int SpecialSlots { get; set; } = 3;
    [JsonPropertyName("specialSlotsUnheard")] public int SpecialSlotsUnheard { get; set; } = 6;
}

public class ExtractsConfig
{
    [JsonPropertyName("chances")] public ChancesConfig? Chances { get; set; }
    [JsonPropertyName("coop")] public CoopConfig? Coop { get; set; }
}

public class ChancesConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // location database name (bigmap, interchange, ...) -> exit chance 0-100
    [JsonPropertyName("locations")] public Dictionary<string, double> Locations { get; set; } = new();
}

public class CoopConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // euros charged for each coop extract
    [JsonPropertyName("amount")] public int Amount { get; set; } = 1000;
}

public class BtrConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // BTR map name (Woods, TarkovStreets, ...) -> settings
    [JsonPropertyName("locations")] public Dictionary<string, BtrLocationConfig> Locations { get; set; } = new();
}

public class BtrLocationConfig
{
    [JsonPropertyName("chance")] public double Chance { get; set; }
    [JsonPropertyName("stopDuration")] public MinMax StopDuration { get; set; } = new();
    [JsonPropertyName("spawnTime")] public MinMax SpawnTime { get; set; } = new();
}

public class MinMax
{
    [JsonPropertyName("min")] public float Min { get; set; }
    [JsonPropertyName("max")] public float Max { get; set; }
}

public class ContainersConfig
{
    [JsonPropertyName("allowItems")] public AllowItemsConfig? AllowItems { get; set; }
}

public class AllowItemsConfig
{
    [JsonPropertyName("thiccItemCase")] public bool ThiccItemCase { get; set; }
    [JsonPropertyName("itemCase")] public bool ItemCase { get; set; }
    [JsonPropertyName("items")] public AllowedGroups? Items { get; set; }
}

public class AllowedGroups
{
    [JsonPropertyName("secureContainers")] public bool SecureContainers { get; set; }
    [JsonPropertyName("ammoBoxes")] public bool AmmoBoxes { get; set; }
    [JsonPropertyName("specials")] public bool Specials { get; set; }
}
