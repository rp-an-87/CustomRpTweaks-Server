using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Modding.Custom;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// Adds the "BTR signal" reactive cartridge: a clone of the white ROP-30 cartridge. The white one has an empty FlareTypes list, so the
// game attaches no effect to it (green = extract, red = airdrop, blue = artillery ...). The client plugin recognises this item by its
// ammo id when the flare succeeds and calls the BTR (BtrFlarePatches). Each reactive cartridge is two items: the weapon-type item in
// the inventory and the ammo it fires, linked through the weapon's defAmmo.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 2)]
public class BtrFlareItems(CustomItemService customItemService, TemplateTable templateTable, ISptLogger<BtrFlareItems> logger, ModHelper modHelper) : IOnLoad
{
    // fixed template ids (the client plugin has the ammo id too)
    public const string CartridgeId = "6a1b7c0d4e5f60718293a4b5";
    public const string AmmoId = "6a1b7c0d4e5f60718293a4b6";

    private const string WhiteCartridge = "62178be9d0050232da3485d9";
    private const string WhiteAmmo = "624c09da2cec124eb67c1046";
    private const string WeaponParent = "5447bedf4bdc2d87278b4568";
    private const string AmmoParent = "5485a8684bdc2da71d8b4567";
    private const string HandbookWeaponParent = "5b5f79eb86f77447ed5636b7";
    private const string HandbookAmmoParent = "5b47574386f77428ca22b33b";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var cfg = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").BtrFlare ?? new BtrFlareConfig();
        if (!cfg.Enabled)
        {
            return Task.CompletedTask;
        }

        var ammo = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = WhiteAmmo,
            ParentId = AmmoParent,
            NewId = AmmoId,
            NewItemName = "patron_rsp_btr_signal",
            HandbookParentId = HandbookAmmoParent,
            HandbookPriceRoubles = 100,
            FleaPriceRoubles = 100,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = "BTR signal flare", ShortName = "BTR flare", Description = "Signal flare of the BTR crew." },
            },
        });
        var cartridge = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = WhiteCartridge,
            ParentId = WeaponParent,
            NewId = CartridgeId,
            NewItemName = "weapon_rsp30_btr_signal",
            OverrideProperties = new TemplateItemProperties { DefAmmo = new MongoId(AmmoId) },
            HandbookParentId = HandbookWeaponParent,
            HandbookPriceRoubles = cfg.HandbookPrice,
            FleaPriceRoubles = cfg.HandbookPrice,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new()
                {
                    Name = "ROP-30 reactive signal cartridge (BTR)",
                    ShortName = "ROP-30 BTR",
                    Description = "Reactive signal cartridge. Fired into the sky, it tells the BTR crew where to pick you up.",
                },
            },
        });

        if (ammo.Success && cartridge.Success)
        {
            var places = AllowWhereWhiteIsAllowed();
            logger.Info($"[CustomRpTweaks] BTR signal cartridge added (item {CartridgeId}, ammo {AmmoId}), allowed in {places} slot/container filters like the white one");
        }
        else
        {
            logger.Error($"[CustomRpTweaks] BTR signal cartridge failed: {string.Join("; ", ammo.Errors.Concat(cartridge.Errors))}");
        }

        return Task.CompletedTask;
    }

    // Slots and containers list what they accept by item id (the pockets' special slots name every flare), so a clone fits nowhere until it is added
    // to the same lists as the white cartridge and its ammo. Returns how many filters it was added to.
    private int AllowWhereWhiteIsAllowed()
    {
        var added = 0;
        var white = new MongoId(WhiteCartridge);
        var whiteAmmo = new MongoId(WhiteAmmo);
        var cartridge = new MongoId(CartridgeId);
        var ammo = new MongoId(AmmoId);

        void Add(HashSet<MongoId>? filter)
        {
            if (filter is null)
            {
                return;
            }

            if (filter.Contains(white) && filter.Add(cartridge))
            {
                added++;
            }
            if (filter.Contains(whiteAmmo) && filter.Add(ammo))
            {
                added++;
            }
        }

        foreach (var item in templateTable.Items.Values)
        {
            foreach (var slot in item.Properties?.Slots ?? [])
            {
                foreach (var filter in slot.Properties?.Filters ?? [])
                {
                    Add(filter.Filter);
                }
            }
            foreach (var grid in item.Properties?.Grids ?? [])
            {
                foreach (var filter in grid.Properties?.Filters ?? [])
                {
                    Add(filter.Filter);
                }
            }
        }

        return added;
    }
}

public class BtrFlareConfig
{
    // adds the BTR signal cartridge (spawn it with admin commands; the client plugin does the BTR call)
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // handbook and flea price of the cartridge, in roubles
    [JsonPropertyName("handbookPrice")] public double HandbookPrice { get; set; } = 15000;
}
