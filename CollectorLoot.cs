using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// The Collector items added by the content backports (QuestBackport / WTT) only get static container spawns, never loose loot.
// This adds them to the loose-loot spawnpoints with the same odds as vanilla Collector items of a similar value: per map, the
// vanilla Collector items nearest in handbook price (that really spawn loose on that map) are the references, and wherever a
// reference spawns, the new item can spawn too, with the average reference share of that spawnpoint's pool.
// It is registered as a lazy-load transformer next to Lots of Loot's, so both apply when the map's loose loot is built.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 4)]
public class CollectorLootApplier(LocationTable locationTable, TemplateTable templateTable, BotTable botTable, ISptLogger<CollectorLootApplier> logger, ModHelper modHelper) : IOnLoad
{
    // vanilla Collector quest items (4.1.6 task list, before the backports)
    private static readonly string[] VanillaCollector =
    [
        "5bc9c1e2d4351e00367fbcf0", "5bc9c049d4351e44f824d360", "5bc9b355d4351e6d1509862a", "5bc9bdb8d4351e003562b8a1",
        "5bc9b9ecd4351e3bac122519", "5bc9b720d4351e450201234b", "5bd073a586f7747e6f135799", "5e54f62086f774219b0f1937",
        "5e54f79686f7744022011103", "5e54f76986f7740366043752", "5e54f6af86f7742199090bf3", "5bc9be8fd4351e00334cae6e",
        "5f745ee30acaeb0d490d8c5b", "60b0f93284c20f0feb453da7", "60b0f7057897d47c5b04ab94", "5fd8d28367cb5e077335170f",
        "60b0f6c058e0b0481a09ad11", "60b0f561c4449e4cb624c1d7", "62a09ec84f842e1bd12da3f2", "62a09e974f842e1bd12da3f0",
        "62a09e73af34e73a266d932a", "62a09e410b9d3c46de5b6e78", "62a09dd4621468534a797ac7", "62a09d79de7ac81993580530",
        "62a09d3bcf4a99369e262447", "62a09cfe4f842e1bd12da3e4", "62a09cb7a04c0c5c6e0a84f8", "62a091170b9d3c46de5b6cf2",
        "62a08f4c4f842e1bd12d9d62", "62a09e08de7ac81993580532", "66b37f114410565a8f6789e2", "66b37eb4acff495a29492407",
        "66b37ea4c5d72b0277488439",
    ];

    private readonly ConcurrentDictionary<string, bool> _logged = new();
    private Dictionary<string, double>? _prices;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var cfg = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").CollectorLoot ?? new CollectorLootConfig();
        if (!cfg.Enabled || cfg.Items.Count == 0)
        {
            return Task.CompletedTask;
        }

        var locations = new Dictionary<string, Location?>
        {
            ["bigmap"] = locationTable.Bigmap,
            ["factory4_day"] = locationTable.Factory4Day,
            ["factory4_night"] = locationTable.Factory4Night,
            ["interchange"] = locationTable.Interchange,
            ["laboratory"] = locationTable.Laboratory,
            ["labyrinth"] = locationTable.Labyrinth,
            ["lighthouse"] = locationTable.Lighthouse,
            ["rezervbase"] = locationTable.RezervBase,
            ["sandbox"] = locationTable.Sandbox,
            ["sandbox_high"] = locationTable.SandboxHigh,
            ["shoreline"] = locationTable.Shoreline,
            ["tarkovstreets"] = locationTable.TarkovStreets,
            ["woods"] = locationTable.Woods,
        };

        foreach (var (name, location) in locations)
        {
            location?.LooseLoot?.AddTransformer(data => Apply(name, data, cfg));
        }

        if (cfg.Bots.Enabled)
        {
            ApplyBots(cfg);
        }

        logger.Info($"[CustomRpTweaks] Collector loot: {cfg.Items.Count} items added to loose loot, odds like the {cfg.ReferenceCount} vanilla Collector items nearest in value (x{cfg.Multiplier})");
        return Task.CompletedTask;
    }

    // Adds the items to the Pockets and Backpack loot pools of the configured bot roles (scavs). The weight is chosen so the item's
    // share of the pool equals the average share of the vanilla Collector items nearest in price that are already in that pool.
    // Progressive Bot System builds its loot cache from these same pools on the first bot of each role and tier, so it picks this up.
    private void ApplyBots(CollectorLootConfig cfg)
    {
        var skip = cfg.Bots.ExcludeItems.ToHashSet();
        var items = cfg.Items.Where(i => !skip.Contains(i)).ToList();
        var vanilla = VanillaCollector.ToHashSet();
        foreach (var role in cfg.Bots.Roles)
        {
            var pools = botTable.Types.GetValueOrDefault(role)?.BotInventory?.Items;
            if (pools is null)
            {
                logger.Warning($"[CustomRpTweaks] Collector loot: bot role '{role}' not found, skipped");
                continue;
            }

            var added = AddToPool(pools.Pockets, items, vanilla, cfg) + AddToPool(pools.Backpack, items, vanilla, cfg);
            logger.Info($"[CustomRpTweaks] Collector loot {role}: {added} pool entries added (pockets, backpack)");
        }
    }

    private int AddToPool(Dictionary<MongoId, double>? pool, List<string> items, HashSet<string> vanilla, CollectorLootConfig cfg)
    {
        if (pool is null)
        {
            return 0;
        }

        var total = pool.Values.Sum();
        var candidates = pool
            .Where(kv => kv.Value > 0 && vanilla.Contains(kv.Key.ToString()))
            .Select(kv => (Tpl: kv.Key.ToString(), Price: PriceOf(kv.Key.ToString()), Weight: kv.Value))
            .Where(c => c.Price is not null)
            .ToList();
        if (total <= 0 || candidates.Count == 0)
        {
            return 0;
        }

        var weights = candidates.ToDictionary(c => c.Tpl, c => c.Weight);
        var additions = new Dictionary<MongoId, double>();
        foreach (var item in items)
        {
            var id = new MongoId(item);
            var price = PriceOf(item);
            if (pool.ContainsKey(id) || price is null)
            {
                continue; // already in this pool (another mod added it) or no price
            }

            var refs = PickReferences(price.Value, candidates.Select(c => (c.Tpl, c.Price!.Value)), cfg.ReferenceCount);
            var share = Math.Min(refs.Sum(r => weights[r] / total) / refs.Count * cfg.Bots.Multiplier, 0.95);
            if (share > 0)
            {
                additions[id] = total * share / (1 - share);
            }
        }

        foreach (var (id, weight) in additions)
        {
            pool[id] = weight;
        }

        return additions.Count;
    }

    private double? PriceOf(string tpl)
    {
        _prices ??= templateTable.Handbook.Items
            .Where(i => i.Price is > 0)
            .GroupBy(i => i.Id.ToString())
            .ToDictionary(g => g.Key, g => g.First().Price!.Value);
        if (_prices.TryGetValue(tpl, out var p))
        {
            return p;
        }

        return templateTable.Prices.TryGetValue(new MongoId(tpl), out var flea) && flea > 0 ? flea : null;
    }

    private LooseLoot? Apply(string map, LooseLoot? data, CollectorLootConfig cfg)
    {
        try
        {
            if (data?.Spawnpoints is null)
            {
                return data;
            }

            var spawnpoints = data.Spawnpoints.ToList();

            // spawnpoints per vanilla item on this map
            var vanilla = VanillaCollector.ToHashSet();
            var counts = new Dictionary<string, int>();
            foreach (var sp in spawnpoints)
            {
                foreach (var tpl in PoolTpls(sp).Where(vanilla.Contains).Distinct())
                {
                    counts[tpl] = counts.GetValueOrDefault(tpl) + 1;
                }
            }

            var candidates = counts
                .Where(kv => kv.Value >= cfg.MinReferenceSpawnpoints)
                .Select(kv => (Tpl: kv.Key, Price: PriceOf(kv.Key)))
                .Where(c => c.Price is not null)
                .Select(c => (c.Tpl, Price: c.Price!.Value))
                .ToList();

            // reference items for each new item
            var refsByItem = new Dictionary<string, HashSet<string>>();
            foreach (var item in cfg.Items)
            {
                if (counts.ContainsKey(item))
                {
                    continue; // already spawns loose on this map (another mod added it)
                }

                var price = PriceOf(item);
                if (price is null)
                {
                    logger.Warning($"[CustomRpTweaks] Collector loot: no price for {item}, skipped");
                    continue;
                }

                refsByItem[item] = PickReferences(price.Value, candidates, cfg.ReferenceCount);
            }

            var added = 0;
            foreach (var sp in spawnpoints)
            {
                if (sp.Template?.Items is null || sp.ItemDistribution is null)
                {
                    continue;
                }

                var items = sp.Template.Items.ToList();
                var dists = sp.ItemDistribution.ToList();
                var keyToTpl = items.Where(i => i.ComposedKey is not null).GroupBy(i => i.ComposedKey!).ToDictionary(g => g.Key, g => g.First().Template.ToString());
                var total = dists.Sum(d => d.RelativeProbability ?? 0);
                if (total <= 0)
                {
                    continue;
                }

                var changed = false;
                foreach (var (item, refs) in refsByItem)
                {
                    var shares = dists
                        .Where(d => d.ComposedKey?.Key is { } k && keyToTpl.TryGetValue(k, out var t) && refs.Contains(t))
                        .Sum(d => (d.RelativeProbability ?? 0) / total);
                    var share = Math.Min(shares / Math.Max(1, refs.Count) * cfg.Multiplier, 0.95);
                    if (share <= 0)
                    {
                        continue;
                    }

                    // weight that makes the new item's share of the pool equal to `share` once it is added
                    var weight = total * share / (1 - share);
                    var key = $"collectorloot-{item}";
                    items.Add(new SptLootItem { Id = new MongoId(), Template = new MongoId(item), ComposedKey = key, Upd = new Upd { StackObjectsCount = 1 } });
                    dists.Add(new LooseLootItemDistribution { ComposedKey = new ComposedKey { Key = key }, RelativeProbability = weight });
                    changed = true;
                    added++;
                }

                if (changed)
                {
                    sp.Template.Items = items;
                    sp.ItemDistribution = dists;
                }
            }

            data.Spawnpoints = spawnpoints;
            if (_logged.TryAdd(map, true))
            {
                var perItem = string.Join(", ", refsByItem.Select(kv => $"{kv.Key[..6]}<-{kv.Value.Count}"));
                logger.Info($"[CustomRpTweaks] Collector loot {map}: {added} spawnpoint entries added ({perItem})");
            }
        }
        catch (Exception e)
        {
            logger.Error($"[CustomRpTweaks] Collector loot failed on {map}: {e}");
        }

        return data;
    }

    private static IEnumerable<string> PoolTpls(Spawnpoint sp)
    {
        if (sp.Template?.Items is null || sp.ItemDistribution is null)
        {
            return [];
        }

        var inDist = sp.ItemDistribution.Select(d => d.ComposedKey?.Key).Where(k => k is not null).ToHashSet();
        return sp.Template.Items.Where(i => i.ComposedKey is not null && inDist.Contains(i.ComposedKey)).Select(i => i.Template.ToString());
    }

    // the `count` candidates closest in price (compared as a ratio, so 20k vs 40k is as far as 40k vs 80k)
    public static HashSet<string> PickReferences(double price, IEnumerable<(string Tpl, double Price)> candidates, int count)
    {
        return candidates
            .OrderBy(c => Math.Abs(Math.Log(c.Price / price)))
            .Take(Math.Max(1, count))
            .Select(c => c.Tpl)
            .ToHashSet();
    }
}

public class CollectorLootConfig
{
    // adds the listed items to loose loot with the odds of vanilla Collector items of similar value
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // scales the resulting odds (1 = same as the reference items)
    [JsonPropertyName("multiplier")] public double Multiplier { get; set; } = 1.0;
    // how many value-nearest vanilla Collector items are averaged per new item
    [JsonPropertyName("referenceCount")] public int ReferenceCount { get; set; } = 5;
    // a vanilla item only counts as a reference on a map where it has at least this many loose spawnpoints
    [JsonPropertyName("minReferenceSpawnpoints")] public int MinReferenceSpawnpoints { get; set; } = 3;
    [JsonPropertyName("items")] public List<string> Items { get; set; } = [];
    [JsonPropertyName("bots")] public CollectorLootBotsConfig Bots { get; set; } = new();
}

public class CollectorLootBotsConfig
{
    // also adds the items to the pockets and backpack loot pools of these bot roles
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // scales the odds against the reference items (1 = same pool share as them)
    [JsonPropertyName("multiplier")] public double Multiplier { get; set; } = 0.5;
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = ["assault", "cursedassault"];
    // items from `items` that bots never get
    [JsonPropertyName("excludeItems")] public List<string> ExcludeItems { get; set; } = [];
}
