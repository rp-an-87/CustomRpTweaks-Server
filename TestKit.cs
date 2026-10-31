using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
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

    // The profiles that get the kit (session ids, or the PMC nickname). Empty: nobody, the log lists the profiles instead.
    [JsonPropertyName("profiles")] public List<string> Profiles { get; set; } = [];
}

// Dev tool for testing GetReady (config.json: "testKit": { "enabled": true }): at server start, puts every missing test item into the
// stash of every profile (the list is docs/getready-test-run.md, Part 2). Items are found by a fixed id (a hash of their key), so a piece still in the stash is never added twice, and
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
    private const string WeaponRepairKit = "5910968f86f77425cf569c32";
    private const string Armor6B13 = "5c0e51be86f774598e797894";
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    private const string Dollars = "5696686a4bdc2da3298b456a";
    private const string Euros = "569668774bdc2da2298b4568";

    // One thing to put in the stash: its items (root first), and the ids to mark found in raid afterwards
    private record Piece(string Key, MongoId RootId, List<Item> Items, HashSet<MongoId> FoundInRaid);

    private static MongoId Id(string key) =>
        new(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("getreadytestkit:" + key)))[..24].ToLowerInvariant());

    private static Piece Single(string key, string tpl, Upd? upd = null, bool fir = false)
    {
        var id = Id(key);
        return new Piece(key, id, [new Item { Id = id, Template = new MongoId(tpl), Upd = upd }], fir ? [id] : []);
    }

    // Several items of one kind (a stack, or a plain count of single items when they do not stack), the first marked found in raid
    // when asked. Stacks over the template's stack size are split into several.
    private List<Piece> Stack(string key, string tpl, int count, bool firFirst = false, Func<Upd>? upd = null)
    {
        var max = templateTable.Items.TryGetValue(new MongoId(tpl), out var template) && template.Properties?.StackMaxSize is > 0 ? template.Properties.StackMaxSize.Value : 1;
        var pieces = new List<Piece>();
        var left = count;
        for (var index = 0; left > 0; index++)
        {
            var size = Math.Min(left, max);
            var piece = Single($"{key}-{index}", tpl, upd?.Invoke() ?? new Upd(), firFirst && index == 0);
            if (size > 1 || max > 1)
            {
                piece.Items[0].Upd ??= new Upd();
                piece.Items[0].Upd!.StackObjectsCount = size;
            }

            pieces.Add(piece);
            left -= size;
        }

        return pieces;
    }

    // A piece with items inside it (a wallet with cash, a case with ammo, a magazine with rounds): the children go in the grid "main"
    // from the top left, one row each, or into the magazine's cartridge slot
    private Piece Holding(string key, string tpl, string slot, params (string Tpl, int Count)[] contents)
    {
        var id = Id(key);
        var items = new List<Item> { new() { Id = id, Template = new MongoId(tpl), Upd = new Upd() } };
        var row = 0;
        foreach (var (childTpl, count) in contents)
        {
            var childId = Id($"{key}-in-{row}");
            items.Add(new Item
            {
                Id = childId,
                Template = new MongoId(childTpl),
                ParentId = id.ToString(),
                SlotId = slot,
                Location = slot == "cartridges" ? (object)row : new ItemLocation { X = 0, Y = row, R = ItemRotation.Horizontal },
                Upd = new Upd { StackObjectsCount = count }
            });
            row++;
        }

        return new Piece(key, id, items, []);
    }

    // The default preset of an armor or a weapon (soft inserts, plates, mods included) with every part worn down to a share of its durability
    private Piece? DamagedPreset(string key, string tpl, double share)
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

    private static Upd Medical(double hp) => new() { MedKit = new UpdMedKit { HpResource = hp } };

    // The list in docs/getready-test-run.md, Part 2 (what can be an item; worn gear is by durability, a hurt character is not an item)
    private List<Piece> BuildKit()
    {
        var kit = new List<Piece?>();
        void Add(params Piece?[] pieces) => kit.AddRange(pieces);
        void AddAll(IEnumerable<Piece> pieces) => kit.AddRange(pieces);

        // Worn armors (the second to test leaving one damaged) and nearly used up repair kits
        Add(DamagedPreset("armor-damaged", Armor6B13, 0.35), DamagedPreset("armor-damaged-2", Armor6B13, 0.6));
        Add(Single("kit-empty", BodyArmorRepairKit, new Upd { RepairKit = new UpdRepairKit { Resource = 2 } }),
            Single("kit-full", BodyArmorRepairKit, new Upd { RepairKit = new UpdRepairKit { Resource = 200 } }),
            Single("wkit-full", WeaponRepairKit, new Upd { RepairKit = new UpdRepairKit { Resource = 200 } }));

        // Medikits: one of each of three kinds (the rules for which kit is picked are unit tests)
        Add(Single("salewa-1", "544fb45d4bdc2dee738b4568"), Single("afak", "60098ad7c2240c0fe85c570a"), Single("grizzly", "590c657e86f77412b013051d"));

        // Bandages, heavy bleed, splints, surgery
        AddAll(Stack("army-bandage", "5751a25924597722c463c472", 2));
        Add(Single("calok", "5e8488fa988a8701445df1e4"), Single("cat", "60098af40accd37ef2175f27"),
            Single("alu-splint-1", "5af0454c86f7746bf20992e8"), Single("imm-splint-1", "544fb3364bdc2d34748b456a"),
            Single("cms-1", "5d02778e86f774203e7dedbe"), Single("cms-2", "5d02778e86f774203e7dedbe"));

        // Injectors (two Propital, one Adrenaline: enough for the case and the special slots) and two injector cases (one for the regular injectors, one empty for labs)
        Add(Single("adrenaline", "5c10c8fd86f7743d7d706df3"), Single("propital-1", "5c0e530286f7747fa1419862"), Single("propital-2", "5c0e530286f7747fa1419862"),
            Single("injector-case", "619cbf7d23893217ec30b689"), Single("injector-case-2", "619cbf7d23893217ec30b689"));

        // Grenades (two kinds, for the random total) and one flare of each colour
        AddAll(Stack("m67", "58d3db5386f77426186285a0", 2));
        AddAll(Stack("rgd5", "5448be9a4bdc2dfd2f8b456a", 2));
        AddAll(Stack("flare-green", "6217726288ed9f0845317459", 1));
        AddAll(Stack("flare-red", "62178c4d4ecf221597654e3d", 1));

        // Cash, a wallet that already holds some, and one cash case
        AddAll(Stack("rub", Roubles, 300000));
        AddAll(Stack("eur", Euros, 3000));
        AddAll(Stack("usd", Dollars, 3000));
        Add(Holding("wallet-holding", "5783c43d2459774bbe137486", "main", (Roubles, 50000)), Single("sicc-1", "5d235bb686f77443f4331278"));

        // Weapons: MCX (.300 BLK), M4A1 (5.56), Glock 18C (full auto), SKS (no full auto); two worn
        Add(DamagedPreset("mcx", "5fbcc1d9016cce60e8341ab3", 0.6), DamagedPreset("m4a1", "5447a9cd4bdc2dbd208b4567", 1.0),
            DamagedPreset("glock18c", "5b1fa9b25acfc40018633c01", 0.5), DamagedPreset("sks", "574d967124597745970e7c94", 1.0));

        // Magazines (empty and with 10 rounds) and ammo in the stash: two kinds of 5.56, .300 BLK and 9x19
        for (var i = 0; i < 4; i++)
        {
            Add(Single($"stanag-{i}", "55d4887d4bdc2d962f8b4570"));
        }

        Add(Holding("stanag-10a", "55d4887d4bdc2d962f8b4570", "cartridges", ("54527ac44bdc2d36668b4567", 10)),
            Single("glock-mag-1", "5a718b548dc32e000d46d262"));
        AddAll(Stack("m855a1", "54527ac44bdc2d36668b4567", 300));
        AddAll(Stack("carry-m855a1", "54527ac44bdc2d36668b4567", 17)); // the loose stack of the baseline layout, in the vest
        AddAll(Stack("m995", "59e690b686f7746c9f75e848", 80));
        AddAll(Stack("blk-ap", "5fd20ff893a8961fc660a954", 120));
        AddAll(Stack("pst", "56d59d3ad2720bdb418b4577", 120));

        // A weapon case with 40 M855A1 inside (for the source order)
        Add(Holding("weapon-case", "59fb023c86f7746d0d4b423c", "main", ("54527ac44bdc2d36668b4567", 40)));

        return kit.OfType<Piece>().ToList();
    }

    // ---- The baseline layout: where the kit sits on you after a reset (what P4 of docs/getready-test-plan.md used to ask by hand)
    private static readonly (string Key, string Slot)[] Worn =
    [
        ("armor-damaged", "ArmorVest"), ("mcx", "FirstPrimaryWeapon"), ("m4a1", "SecondPrimaryWeapon"), ("glock18c", "Holster")
    ];

    private static readonly (string Slot, string[] Keys)[] Carried =
    [
        ("TacticalVest", ["stanag-10a", "stanag-0", "carry-m855a1-0"]),
        ("Backpack", ["stanag-1", "stanag-2"]),
        ("Pockets", ["injector-case"])
    ];

    private static readonly string[] SlungWeapons = ["mcx", "m4a1", "glock18c"];

    // The item and every item below it
    private static void RemoveWithChildren(List<Item> items, MongoId id)
    {
        var doomed = new HashSet<MongoId> { id };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var item in items.Where(item => item.ParentId != null && !doomed.Contains(item.Id) && doomed.Any(d => d.ToString() == item.ParentId)).ToList())
            {
                doomed.Add(item.Id);
                grew = true;
            }
        }

        items.RemoveAll(item => doomed.Contains(item.Id));
    }

    // The first free place of any grid of the container for the item (as it lies, not turned), keeping what is there; false when there is none
    private bool PlaceInContainer(List<Item> items, Item container, Item item)
    {
        if (!templateTable.Items.TryGetValue(container.Template, out var template) || template.Properties?.Grids == null)
        {
            return false;
        }

        var (width, height) = inventoryHelper.GetItemSize(item.Template, item.Id, items);
        foreach (var grid in template.Properties.Grids)
        {
            var columns = grid.Properties?.CellsH ?? 0;
            var rows = grid.Properties?.CellsV ?? 0;
            if (columns <= 0 || rows <= 0 || grid.Name == null)
            {
                continue;
            }

            var taken = new bool[rows, columns];
            foreach (var other in items.Where(other => other.ParentId == container.Id.ToString() && other.SlotId == grid.Name && other.Id != item.Id))
            {
                var location = other.GetParsedLocation();
                if (location?.X == null || location.Y == null)
                {
                    continue;
                }

                var (otherWidth, otherHeight) = inventoryHelper.GetItemSize(other.Template, other.Id, items);
                var turned = location.IsVertical();
                for (var y = 0; y < (turned ? otherWidth : otherHeight); y++)
                {
                    for (var x = 0; x < (turned ? otherHeight : otherWidth); x++)
                    {
                        if (location.Y.Value + y < rows && location.X.Value + x < columns)
                        {
                            taken[location.Y.Value + y, location.X.Value + x] = true;
                        }
                    }
                }
            }

            for (var y = 0; y + height <= rows; y++)
            {
                for (var x = 0; x + width <= columns; x++)
                {
                    var free = true;
                    for (var dy = 0; dy < height && free; dy++)
                    {
                        for (var dx = 0; dx < width && free; dx++)
                        {
                            free = !taken[y + dy, x + dx];
                        }
                    }

                    if (!free)
                    {
                        continue;
                    }

                    item.ParentId = container.Id.ToString();
                    item.SlotId = grid.Name;
                    item.Location = new ItemLocation { X = x, Y = y, R = ItemRotation.Horizontal, Rotation = false };
                    return true;
                }
            }
        }

        return false;
    }

    // Puts the kit where the test chains start: the armor worn, the three weapons slung with no magazine in them, two magazines and a
    // loose stack in the vest, two magazines in the backpack, the injector case in the pockets. Only kit items are moved; whatever
    // else sits in a place is left as it is and said in the notes. Returns how many things changed.
    private int ApplyLayout(PmcData pmc, List<Piece> kit, List<string> notes)
    {
        var inventory = pmc.Inventory;
        if (inventory?.Items == null || inventory.Equipment == null || inventory.Stash == null)
        {
            return 0;
        }

        var items = inventory.Items;
        var equipment = inventory.Equipment.Value.ToString();
        var kitRoots = kit.Select(piece => piece.RootId).ToHashSet();
        var changes = 0;

        Item? Root(string key)
        {
            var piece = kit.FirstOrDefault(candidate => candidate.Key == key);
            return piece == null ? null : items.FirstOrDefault(item => item.Id == piece.RootId);
        }

        // The weapons carry no magazine
        foreach (var key in SlungWeapons)
        {
            var weapon = Root(key);
            if (weapon == null)
            {
                continue;
            }

            foreach (var magazine in items.Where(item => item.ParentId == weapon.Id.ToString() && item.SlotId == "mod_magazine").Select(item => item.Id).ToList())
            {
                RemoveWithChildren(items, magazine);
                changes++;
            }
        }

        // Worn: a kit piece in the way goes to the stash, anything else stays and is said
        var stash = items.FirstOrDefault(item => item.Id == inventory.Stash.Value);
        foreach (var (key, slot) in Worn)
        {
            var item = Root(key);
            if (item == null || (item.ParentId == equipment && item.SlotId == slot))
            {
                continue;
            }

            var occupant = items.FirstOrDefault(other => other.ParentId == equipment && other.SlotId == slot);
            if (occupant != null)
            {
                if (!kitRoots.Contains(occupant.Id))
                {
                    notes.Add($"{slot} holds something that is not kit gear, so {key} stays where it is");
                    continue;
                }

                if (stash == null || !PlaceInContainer(items, stash, occupant))
                {
                    notes.Add($"no room in the stash to take the kit piece off {slot}, so {key} stays where it is");
                    continue;
                }

                changes++;
            }

            item.ParentId = equipment;
            item.SlotId = slot;
            item.Location = null;
            changes++;
        }

        // Carried
        foreach (var (slot, keys) in Carried)
        {
            var container = items.FirstOrDefault(item => item.ParentId == equipment && item.SlotId == slot);
            if (container == null)
            {
                notes.Add($"nothing is worn in {slot}, so its kit items stay where they are");
                continue;
            }

            foreach (var key in keys)
            {
                var item = Root(key);
                if (item == null || item.ParentId == container.Id.ToString())
                {
                    continue;
                }

                if (PlaceInContainer(items, container, item))
                {
                    changes++;
                }
                else
                {
                    notes.Add($"no room in {slot} for {key}");
                }
            }
        }

        return changes;
    }

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        await ApplyAsync(null, cancellationToken);
    }

    private const string MagazineParent = "5448bc234bdc2d3c308b4569";

    // Every magazine of the kit (loose, or inside a kit weapon) gets its cartridges back as the kit made them: other rounds come out,
    // missing ones return. Returns how many magazines were changed.
    private int ResetMagazines(List<Item> items, List<Piece> kit)
    {
        var all = kit.SelectMany(piece => piece.Items).ToList();
        var magazineIds = all.Where(item => templateTable.Items.TryGetValue(item.Template, out var template) && template.Parent == MagazineParent).Select(item => item.Id).ToHashSet();
        var changed = 0;
        foreach (var magazineId in magazineIds.Where(id => items.Any(item => item.Id == id)))
        {
            var parent = magazineId.ToString();
            var current = items.Where(item => item.ParentId == parent && item.SlotId == "cartridges").ToList();
            var wanted = all.Where(item => item.ParentId == parent && item.SlotId == "cartridges").ToList();
            if (current.Count == wanted.Count && wanted.All(want => current.Any(have => have.Id == want.Id && have.Template == want.Template && have.Upd?.StackObjectsCount == want.Upd?.StackObjectsCount)))
            {
                continue;
            }

            items.RemoveAll(item => current.Contains(item) || wanted.Any(want => want.Id == item.Id));
            foreach (var want in wanted)
            {
                items.Add(want with { Upd = want.Upd == null ? null : want.Upd with { } });
            }

            changed++;
        }

        return changed;
    }

    // Hurt or heal the profile that asks (only profiles in testKit.profiles, only when the kit is on)
    public async Task<string> SetHealthAsync(MongoId sessionId, bool hurt)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").TestKit;
        var nickname = saveServer.GetProfile(sessionId)?.CharacterData?.PmcData?.Info?.Nickname ?? "";
        if (config is null || !config.Enabled
            || !config.Profiles.Any(entry => string.Equals(entry, sessionId.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(entry, nickname, StringComparison.OrdinalIgnoreCase)))
        {
            return "The test kit is off or this profile is not in testKit.profiles";
        }

        var answer = await TestHealth.SetAsync(saveServer, sessionId, hurt);
        logger.Info($"[CustomRpTweaks] Test kit: {answer} ({sessionId})");
        return answer;
    }

    // The kit for the profiles listed in the config (or only for one session: the reset route of the game's F12 button). The config is
    // read again each time, so switching it on or off needs no restart. The answer says what happened, for the client.
    public async Task<string> ApplyAsync(MongoId? only, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").TestKit;
        if (config is null || !config.Enabled)
        {
            return "The test kit is off (testKit.enabled in the CustomRpTweaks config.json)";
        }

        var profilesPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(modPath, "..", "..", "profiles"));
        var kit = BuildKit();

        foreach (var (sessionId, profile) in saveServer.GetProfiles())
        {
            if (only != null && sessionId != only.Value)
            {
                continue;
            }

            var pmc = profile.CharacterData?.PmcData;
            if (pmc?.Inventory?.Items == null || pmc.Inventory.Stash == null)
            {
                continue;
            }

            var nickname = pmc.Info?.Nickname ?? "";
            if (!config.Profiles.Any(entry => string.Equals(entry, sessionId.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(entry, nickname, StringComparison.OrdinalIgnoreCase)))
            {
                logger.Info($"[CustomRpTweaks] Test kit: skipped profile {sessionId} ({nickname}); add its id or nickname to testKit.profiles to give it the kit");
                results.Add($"Profile {nickname} is not in testKit.profiles");
                continue;
            }

            // The rounds in the kit's magazines go back to what the kit put there (before looking for what is missing, because a kit
            // stack you loaded into a magazine lies there as a cartridge and must come out of it)
            var reloaded = ResetMagazines(pmc.Inventory.Items, kit);

            var byId = pmc.Inventory.Items.ToDictionary(item => item.Id);
            var missing = kit.Where(piece => !byId.ContainsKey(piece.RootId)).ToList();

            // What is still there gets worn down again (durability, kit resource), so a repaired piece needs no throwing away
            var reset = 0;
            foreach (var original in kit.Where(piece => byId.ContainsKey(piece.RootId)).SelectMany(piece => piece.Items))
            {
                var stack = original.SlotId != "cartridges" ? original.Upd?.StackObjectsCount : null;
                if (!byId.TryGetValue(original.Id, out var current) || (original.Upd?.Repairable == null && original.Upd?.RepairKit == null && original.Upd?.MedKit == null && stack == null))
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

                if (stack != null && current.Upd.StackObjectsCount != stack)
                {
                    current.Upd.StackObjectsCount = stack;
                    reset++;
                }

                if (original.Upd.MedKit != null && current.Upd.MedKit?.HpResource != original.Upd.MedKit.HpResource)
                {
                    current.Upd.MedKit = original.Upd.MedKit with { };
                    reset++;
                }
            }

            reset += reloaded;
            if (missing.Count == 0)
            {
                var layoutNotes = new List<string>();
                var laidOut = ApplyLayout(pmc, kit, layoutNotes);
                if (reset > 0 || laidOut > 0)
                {
                    await saveServer.SaveProfileAsync(sessionId, cancellationToken);
                }

                var layoutText = $"layout: {laidOut} change(s){(layoutNotes.Count > 0 ? " (" + string.Join("; ", layoutNotes) + ")" : "")}";
                logger.Info($"[CustomRpTweaks] Test kit: all of it is in the stash of {sessionId}, worn down again: {reset} value(s), {layoutText}");
                results.Add($"Nothing missing; worn down again: {reset} value(s); {layoutText}");
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

            var notes = new List<string>();
            var arranged = ApplyLayout(pmc, kit, notes);
            await saveServer.SaveProfileAsync(sessionId, cancellationToken);
            var arrangedText = $"layout: {arranged} change(s){(notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "")}";
            logger.Info($"[CustomRpTweaks] Test kit: added {added} of {missing.Count} missing items to the stash of {sessionId}, worn down again: {reset} value(s), {arrangedText} (profile backup in the mod's backups folder)");
            results.Add($"Added {added} of {missing.Count} missing items; worn down again: {reset} value(s); {arrangedText}");
        }

        return results.Count == 0 ? "No profile with an inventory was found" : string.Join("; ", results);
    }
}

// Health for the tests, without a raid: "hurt" leaves a fracture, a light and a heavy bleed and missing HP; "heal" makes every body part
// full and clears the effects. The profile file is changed in memory and saved; the client reloads its menu afterwards.
public static class TestHealth
{
    public static async Task<string> SetAsync(SaveServer saveServer, MongoId sessionId, bool hurt)
    {
        var health = saveServer.GetProfile(sessionId)?.CharacterData?.PmcData?.Health;
        if (health?.BodyParts == null)
        {
            return "No health data in the profile";
        }

        // body part -> HP left and the effect on it (hurt only)
        var hurtState = new Dictionary<string, (double Hp, string? Effect)>
        {
            ["LeftLeg"] = (15, "Fracture"),
            ["RightArm"] = (20, "LightBleeding"),
            ["Chest"] = (45, "HeavyBleeding"),
            ["Stomach"] = (40, null),
        };

        foreach (var (name, part) in health.BodyParts)
        {
            if (part.Health == null)
            {
                continue;
            }

            part.Effects = new Dictionary<string, BodyPartEffectProperties>();
            if (hurt && hurtState.TryGetValue(name, out var state))
            {
                part.Health.Current = state.Hp;
                if (state.Effect != null)
                {
                    part.Effects[state.Effect] = new BodyPartEffectProperties { Time = -1 };
                }
            }
            else
            {
                part.Health.Current = part.Health.Maximum;
            }
        }

        await saveServer.SaveProfileAsync(sessionId);
        return hurt ? "Hurt: left leg fractured, right arm light bleed, chest heavy bleed, low HP" : "Healed: every body part full, no effects";
    }
}

// The route of the game's F12 button (GetReady > Advanced): puts the kit back for the profile that asks, no server restart needed.
// The client reloads its profile afterwards. It only answers for profiles listed in testKit.profiles.
[Injectable]
public class TestKitRouter : StaticRouter
{
    private static TestKit? _kit;

    public TestKitRouter(JsonUtil jsonUtil, TestKit kit) : base(jsonUtil, GetRoutes())
    {
        _kit = kit;
    }

    private static List<RouteAction> GetRoutes() =>
    [
        new RouteAction(
            "/customrptweaks/testkit/reset",
            async (url, request, sessionId, _, _) => await _kit!.ApplyAsync(sessionId, CancellationToken.None)),
        new RouteAction(
            "/customrptweaks/testkit/hurt",
            async (url, request, sessionId, _, _) => await _kit!.SetHealthAsync(sessionId, true)),
        new RouteAction(
            "/customrptweaks/testkit/heal",
            async (url, request, sessionId, _, _) => await _kit!.SetHealthAsync(sessionId, false)),
    ];
}
