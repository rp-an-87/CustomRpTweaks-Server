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
public class BtrFlareItems(CustomItemService customItemService, TemplateTable templateTable, TradersTable tradersTable, ISptLogger<BtrFlareItems> logger, ModHelper modHelper) : IOnLoad
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
            AddTraderOffers(cfg.Trader);
        }
        else
        {
            logger.Error($"[CustomRpTweaks] BTR signal cartridge failed: {string.Join("; ", ammo.Errors.Concat(cartridge.Errors))}");
        }

        return Task.CompletedTask;
    }

    // The trader sells the cartridge to anyone: for roubles, and for a barter. What keeps it useless early is the BTR driver standing check in the client.
    // Ids of the two offers are fixed so they stay the same between restarts.
    private const string RoublesId = "5449016a4bdc2d6f028b456f";
    private static readonly MongoId RoubleOfferId = new("6a1b7c0d4e5f60718293a4b7");
    private static readonly MongoId BarterOfferId = new("6a1b7c0d4e5f60718293a4b8");

    private void AddTraderOffers(BtrFlareTraderConfig trader)
    {
        if (!trader.Enabled)
        {
            return;
        }

        var assort = tradersTable.GetTrader(new MongoId(trader.TraderId))?.Assort;
        if (assort is null)
        {
            logger.Warning($"[CustomRpTweaks] BTR signal cartridge: trader {trader.TraderId} not found, no offers added");
            return;
        }

        if (trader.RoublePrice > 0)
        {
            AddOffer(assort, RoubleOfferId, trader, [[new BarterScheme { Template = new MongoId(RoublesId), Count = trader.RoublePrice }]]);
        }

        var barter = trader.Barter.Where(b => b.Count > 0 && !string.IsNullOrWhiteSpace(b.Item)).Select(b => new BarterScheme { Template = new MongoId(b.Item), Count = b.Count }).ToList();
        if (barter.Count > 0)
        {
            AddOffer(assort, BarterOfferId, trader, [barter]);
        }

        logger.Info($"[CustomRpTweaks] BTR signal cartridge offered by trader {trader.TraderId} at loyalty level {trader.LoyaltyLevel}: {(trader.RoublePrice > 0 ? trader.RoublePrice + " RUB" : "no rouble offer")}, {(barter.Count > 0 ? "barter " + string.Join(" + ", barter.Select(b => b.Count + "x " + b.Template)) : "no barter offer")}");
    }

    private static void AddOffer(TraderAssort assort, MongoId offerId, BtrFlareTraderConfig trader, List<List<BarterScheme>> scheme)
    {
        assort.Items.RemoveAll(item => item.Id == offerId);
        assort.Items.Add(new Item
        {
            Id = offerId,
            Template = new MongoId(CartridgeId),
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd
            {
                UnlimitedCount = true,
                StackObjectsCount = 9999999,
                BuyRestrictionMax = trader.BuyLimit,
                BuyRestrictionCurrent = 0,
            },
        });
        assort.BarterScheme[offerId] = scheme;
        assort.LoyalLevelItems[offerId] = trader.LoyaltyLevel;
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
    // the trader sells it (an offer for roubles and one for a barter)
    [JsonPropertyName("trader")] public BtrFlareTraderConfig Trader { get; set; } = new();
}

public class BtrFlareTraderConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    // Prapor
    [JsonPropertyName("traderId")] public string TraderId { get; set; } = "54cb50c76803fa8b248b4571";
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 2;
    // roubles for the rouble offer, 0 = no rouble offer
    [JsonPropertyName("roublePrice")] public double RoublePrice { get; set; } = 20000;
    // how many a profile can buy per trader restock
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 2;
    // the barter offer: everything in the list at once, an empty list = no barter offer. Default: a white reactive cartridge (what it is made from) and military cable
    [JsonPropertyName("barter")] public List<BtrBarterItem> Barter { get; set; } = [new() { Item = "62178be9d0050232da3485d9", Count = 1 }, new() { Item = "5d0375ff86f774186372f685", Count = 1 }];
}

public class BtrBarterItem
{
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    [JsonPropertyName("count")] public double Count { get; set; } = 1;
}
