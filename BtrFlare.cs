using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
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
public class BtrFlareItems(CustomItemService customItemService, TemplateTable templateTable, TradersTable tradersTable, RagfairConfig ragfairConfig, ISptLogger<BtrFlareItems> logger, ModHelper modHelper) : IOnLoad
{
    // fixed template ids (the client plugin has the ammo id too)
    public const string CartridgeId = "6a1b7c0d4e5f60718293a4b5";
    public const string AmmoId = "6a1b7c0d4e5f60718293a4b6";
    // the same flare as a loose 26x75 round for the ZiD SP-81 signal pistol (the gun is kept, the rounds are bought)
    public const string PistolAmmoId = "6a1b7c0d4e5f60718293a4b9";

    private const string WhiteCartridge = "62178be9d0050232da3485d9";
    private const string WhiteAmmo = "624c09da2cec124eb67c1046";
    private const string WhitePistolAmmo = "62389bc9423ed1685422dc57";
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
            AddToFleaPriceDb = false,
            AddToWeaponShelf = false,
            OverrideProperties = new TemplateItemProperties { CanSellOnRagfair = false, BackgroundColor = "violet" },
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
            OverrideProperties = new TemplateItemProperties { DefAmmo = new MongoId(AmmoId), CanSellOnRagfair = false, BackgroundColor = "violet" },
            HandbookParentId = HandbookWeaponParent,
            HandbookPriceRoubles = cfg.HandbookPrice,
            AddToFleaPriceDb = false,
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

        var pistolAmmo = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = WhitePistolAmmo,
            ParentId = AmmoParent,
            NewId = PistolAmmoId,
            NewItemName = "patron_26x75_btr_signal",
            HandbookParentId = HandbookAmmoParent,
            HandbookPriceRoubles = cfg.AmmoHandbookPrice,
            AddToFleaPriceDb = false,
            AddToWeaponShelf = false,
            OverrideProperties = new TemplateItemProperties { CanSellOnRagfair = false, BackgroundColor = "violet" },
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = "26x75mm BTR signal flare cartridge", ShortName = "BTR flare", Description = "Signal flare for the ZiD SP-81 signal pistol. Fired into the sky, it tells the BTR crew where to pick you up." },
            },
        });

        if (ammo.Success && cartridge.Success && pistolAmmo.Success)
        {
            var places = AllowWhereWhiteIsAllowed();
            logger.Info($"[CustomRpTweaks] BTR signal cartridge added (item {CartridgeId}, ammo {AmmoId}) and signal pistol round {PistolAmmoId}, allowed in {places} slot/container filters like the white ones");
            AddTraderOffers(cfg.Trader);
            KeepOffTheFlea();
        }
        else
        {
            logger.Error($"[CustomRpTweaks] BTR signal cartridge failed: {string.Join("; ", ammo.Errors.Concat(cartridge.Errors).Concat(pistolAmmo.Errors))}");
        }

        return Task.CompletedTask;
    }

    // No generated flea offers and no player listing for these (the custom flea blacklist, CanSellOnRagfair = false, no flea price). The flea still lists the trader's own offers (the server does not hide trader offers of banned items), which is
    // what flea searches (linked search, filter by item) find; only the trader sells them.
    private void KeepOffTheFlea()
    {
        ragfairConfig.Dynamic.Blacklist.Custom.Add(new MongoId(CartridgeId));
        ragfairConfig.Dynamic.Blacklist.Custom.Add(new MongoId(AmmoId));
        ragfairConfig.Dynamic.Blacklist.Custom.Add(new MongoId(PistolAmmoId));
    }

    // The trader sells the cartridge to anyone: for a barter (two military cables) from the low level, for roubles only from a high one (the same for the
    // signal pistol round). What keeps it useless early is the BTR driver standing check in the client. Ids of the offers are fixed so they stay the same
    // between restarts.
    private const string RoublesId = "5449016a4bdc2d6f028b456f";
    private static readonly MongoId RoubleOfferId = new("6a1b7c0d4e5f60718293a4b7");
    private static readonly MongoId BarterOfferId = new("6a1b7c0d4e5f60718293a4b8");
    private static readonly MongoId AmmoRoubleOfferId = new("6a1b7c0d4e5f60718293a4c1");
    private static readonly MongoId AmmoBarterOfferId = new("6a1b7c0d4e5f60718293a4c2");

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

        AddOffers(assort, trader, "cartridge", CartridgeId, RoubleOfferId, BarterOfferId, trader.RoublePrice, trader.Barter, trader.BuyLimit);
        AddOffers(assort, trader, "signal pistol round", PistolAmmoId, AmmoRoubleOfferId, AmmoBarterOfferId, trader.AmmoRoublePrice, trader.AmmoBarter, trader.AmmoBuyLimit);
    }

    private void AddOffers(TraderAssort assort, BtrFlareTraderConfig trader, string what, string templateId, MongoId roubleOfferId, MongoId barterOfferId, double roublePrice, List<BtrBarterItem> barterItems, int buyLimit)
    {
        if (roublePrice > 0)
        {
            AddOffer(assort, roubleOfferId, templateId, trader.RoubleLoyaltyLevel, buyLimit, [[new BarterScheme { Template = new MongoId(RoublesId), Count = roublePrice }]]);
        }

        var barter = barterItems.Where(b => b.Count > 0 && !string.IsNullOrWhiteSpace(b.Item)).Select(b => new BarterScheme { Template = new MongoId(b.Item), Count = b.Count }).ToList();
        if (barter.Count > 0)
        {
            AddOffer(assort, barterOfferId, templateId, trader.LoyaltyLevel, buyLimit, [barter]);
        }

        logger.Info($"[CustomRpTweaks] BTR signal {what} offered by trader {trader.TraderId}: {(roublePrice > 0 ? roublePrice + " RUB at loyalty level " + trader.RoubleLoyaltyLevel : "no rouble offer")}, {(barter.Count > 0 ? "barter at loyalty level " + trader.LoyaltyLevel + ": " + string.Join(" + ", barter.Select(b => b.Count + "x " + b.Template)) : "no barter offer")}");
    }

    private static void AddOffer(TraderAssort assort, MongoId offerId, string templateId, int loyaltyLevel, int buyLimit, List<List<BarterScheme>> scheme)
    {
        assort.Items.RemoveAll(item => item.Id == offerId);
        assort.Items.Add(new Item
        {
            Id = offerId,
            Template = new MongoId(templateId),
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd
            {
                UnlimitedCount = true,
                StackObjectsCount = 9999999,
                BuyRestrictionMax = buyLimit,
                BuyRestrictionCurrent = 0,
            },
        });
        assort.BarterScheme[offerId] = scheme;
        assort.LoyalLevelItems[offerId] = loyaltyLevel;
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
        var whitePistol = new MongoId(WhitePistolAmmo);
        var pistolAmmo = new MongoId(PistolAmmoId);

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
            if (filter.Contains(whitePistol) && filter.Add(pistolAmmo))
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
            // chambers (the signal pistol's) and magazine cartridge slots list their ammo too
            foreach (var slot in (item.Properties?.Chambers ?? []).Concat(item.Properties?.Cartridges ?? []))
            {
                foreach (var filter in slot.Properties?.Filters ?? [])
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
    // handbook price of the loose signal pistol round (ZiD SP-81), in roubles
    [JsonPropertyName("ammoHandbookPrice")] public double AmmoHandbookPrice { get; set; } = 10000;
    // the trader sells it (an offer for roubles and one for a barter)
    [JsonPropertyName("trader")] public BtrFlareTraderConfig Trader { get; set; } = new();
}

public class BtrFlareTraderConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    // Prapor
    [JsonPropertyName("traderId")] public string TraderId { get; set; } = "54cb50c76803fa8b248b4571";
    // loyalty level of the barter offer
    [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; } = 2;
    // loyalty level of the rouble offer
    [JsonPropertyName("roubleLoyaltyLevel")] public int RoubleLoyaltyLevel { get; set; } = 4;
    // roubles for the rouble offer, 0 = no rouble offer
    [JsonPropertyName("roublePrice")] public double RoublePrice { get; set; } = 100830;
    // how many a profile can buy per trader restock
    [JsonPropertyName("buyLimit")] public int BuyLimit { get; set; } = 2;
    // the barter offer: everything in the list at once, an empty list = no barter offer. Default: two military cables
    // the loose 26x75 round for the ZiD SP-81 signal pistol: same trader and level, own price, limit and barter (two military cables)
    [JsonPropertyName("ammoRoublePrice")] public double AmmoRoublePrice { get; set; } = 99470;
    [JsonPropertyName("ammoBuyLimit")] public int AmmoBuyLimit { get; set; } = 4;
    [JsonPropertyName("ammoBarter")] public List<BtrBarterItem> AmmoBarter { get; set; } = [new() { Item = "5d0375ff86f774186372f685", Count = 2 }];
    [JsonPropertyName("barter")] public List<BtrBarterItem> Barter { get; set; } = [new() { Item = "5d0375ff86f774186372f685", Count = 2 }];
}

public class BtrBarterItem
{
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    [JsonPropertyName("count")] public double Count { get; set; } = 1;
}
