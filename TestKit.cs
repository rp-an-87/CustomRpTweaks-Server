using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Utils;

namespace CustomRpTweaks;

public class TestKitConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

// Dev tool for testing GetReady (config.json: "testKit": { "enabled": true }): at server start, puts every missing test item into the
// stash of every profile. Items are found by a fixed id (a hash of their key), so a piece still in the stash is never added twice, and
// one you used up comes back at the next start, and pieces still there are worn down again. The profile file is copied to this mod's "backups" folder first. Off by default.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 20)]
public class TestKit(
    SaveServer saveServer,
    InventoryHelper inventoryHelper,
    PresetHelper presetHelper,
    TemplateTable templateTable,
    EventOutputHolder eventOutputHolder,
    ISptLogger<TestKit> logger,
    ModHelper modHelper) : IOnLoad
{
    private const string BodyArmorRepairKit = "591094e086f7747caa7bb2ef";
    private const string Armor6B13 = "5c0e51be86f774598e797894";

    // One thing to put in the stash: its items (root first), and the ids to mark found in raid afterwards
    private record Piece(string Key, MongoId RootId, List<Item> Items, HashSet<MongoId> FoundInRaid);

    private static MongoId Id(string key) =>
        new(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("getreadytestkit:" + key)))[..24].ToLowerInvariant());

    private static Piece Single(string key, string tpl, Upd? upd = null, bool fir = false)
    {
        var id = Id(key);
        return new Piece(key, id, [new Item { Id = id, Template = new MongoId(tpl), Upd = upd }], fir ? [id] : []);
    }

    // The default preset of an armor (soft inserts and plates included) with every part worn down to a share of its durability
    private Piece? DamagedArmor(string key, string tpl, double share)
    {
        var preset = presetHelper.GetDefaultPreset(new MongoId(tpl));
        if (preset?.Items == null || preset.Items.Count == 0)
        {
            logger.Warning($"[CustomRpTweaks] Test kit: no default preset for {tpl}, skipping {key}");
            return null;
        }

        var map = preset.Items.ToDictionary(item => item.Id.ToString(), item => Id(key + "-" + item.Id));
        var items = new List<Item>();
        foreach (var source in preset.Items)
        {
            var item = source with { Id = map[source.Id.ToString()], Upd = source.Upd == null ? new Upd() : source.Upd with { } };
            if (source.ParentId != null && map.TryGetValue(source.ParentId, out var mapped))
            {
                item.ParentId = mapped.ToString();
            }

            if (templateTable.Items.TryGetValue(item.Template, out var template) && template.Properties?.MaxDurability is > 0)
            {
                var max = template.Properties.MaxDurability.Value;
                item.Upd.Repairable = new UpdRepairable { Durability = Math.Round(max * share, 1), MaxDurability = max };
            }

            items.Add(item);
        }

        // The root of a preset has no parent: the stash placement sets it
        items[0].ParentId = null;
        items[0].SlotId = null;
        items[0].Location = null;
        return new Piece(key, items[0].Id, items, []);
    }

    // Only what is hard to get in the game: worn armor and repair kits that are nearly used up. Ammo, meds and the rest you have anyway.
    private List<Piece> BuildKit()
    {
        var kit = new List<Piece?>
        {
            // Two worn armors (the second to test leaving one damaged), and body armor repair kits with little and almost no resource
            DamagedArmor("armor-damaged", Armor6B13, 0.35),
            DamagedArmor("armor-damaged-2", Armor6B13, 0.6),
            Single("kit-low", BodyArmorRepairKit, new Upd { RepairKit = new UpdRepairKit { Resource = 40 } }),
            Single("kit-empty", BodyArmorRepairKit, new Upd { RepairKit = new UpdRepairKit { Resource = 2 } }),
        };

        return kit.OfType<Piece>().ToList();
    }

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").TestKit;
        if (config is null || !config.Enabled)
        {
            return;
        }

        var profilesPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(modPath, "..", "..", "profiles"));
        var kit = BuildKit();

        foreach (var (sessionId, profile) in saveServer.GetProfiles())
        {
            var pmc = profile.CharacterData?.PmcData;
            if (pmc?.Inventory?.Items == null || pmc.Inventory.Stash == null)
            {
                continue;
            }

            var byId = pmc.Inventory.Items.ToDictionary(item => item.Id);
            var missing = kit.Where(piece => !byId.ContainsKey(piece.RootId)).ToList();

            // What is still there gets worn down again (durability, kit resource), so a repaired piece needs no throwing away
            var reset = 0;
            foreach (var original in kit.Where(piece => byId.ContainsKey(piece.RootId)).SelectMany(piece => piece.Items))
            {
                if (!byId.TryGetValue(original.Id, out var current) || (original.Upd?.Repairable == null && original.Upd?.RepairKit == null))
                {
                    continue;
                }

                current.Upd ??= new Upd();
                if (original.Upd.Repairable != null && current.Upd.Repairable?.Durability != original.Upd.Repairable.Durability)
                {
                    current.Upd.Repairable = original.Upd.Repairable with { };
                    reset++;
                }

                if (original.Upd.RepairKit != null && current.Upd.RepairKit?.Resource != original.Upd.RepairKit.Resource)
                {
                    current.Upd.RepairKit = original.Upd.RepairKit with { };
                    reset++;
                }
            }

            if (missing.Count == 0)
            {
                if (reset > 0)
                {
                    await saveServer.SaveProfileAsync(sessionId, cancellationToken);
                }

                logger.Info($"[CustomRpTweaks] Test kit: all of it is in the stash of {sessionId}, worn down again: {reset} value(s)");
                continue;
            }

            // A copy of the profile as it was, before anything is added
            var source = System.IO.Path.Combine(profilesPath, $"{sessionId}.json");
            if (File.Exists(source))
            {
                var backups = System.IO.Path.Combine(modPath, "backups");
                Directory.CreateDirectory(backups);
                File.Copy(source, System.IO.Path.Combine(backups, $"{sessionId}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
            }

            // Added as not found in raid; the pieces that should be get the mark afterwards (the stash helper sets it for a whole item)
            var output = eventOutputHolder.GetOutput(sessionId);
            var added = 0;
            foreach (var piece in missing)
            {
                var request = new AddItemsDirectRequest { ItemsWithModsToAdd = [piece.Items], FoundInRaid = false, UseSortingTable = false };
                inventoryHelper.AddItemsToStash(sessionId, request, pmc, output);
                if (output.Warnings?.Count > 0)
                {
                    logger.Warning($"[CustomRpTweaks] Test kit: {piece.Key} could not be placed (no room in the stash?), stopping");
                    break;
                }

                added++;
            }

            eventOutputHolder.ResetOutput(sessionId);

            var fir = missing.SelectMany(piece => piece.FoundInRaid).ToHashSet();
            foreach (var item in pmc.Inventory.Items.Where(item => fir.Contains(item.Id)))
            {
                item.Upd ??= new Upd();
                item.Upd.SpawnedInSession = true;
            }

            await saveServer.SaveProfileAsync(sessionId, cancellationToken);
            logger.Info($"[CustomRpTweaks] Test kit: added {added} of {missing.Count} missing items to the stash of {sessionId}, worn down again: {reset} value(s) (profile backup in the mod's backups folder)");
        }
    }
}
