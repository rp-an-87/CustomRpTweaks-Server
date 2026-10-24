using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// When you use up your personal purchase limit on a trader offer (or the offer sells out), you can buy it again right away
// at a higher price, until the trader's real restock. Nearly every offer has unlimited stock, so the limit is what ends a round.
// Rounds and prices are kept per profile and applied to the assort each profile is sent. This patches TradeHelper.BuyItem (a
// purchase), TraderAssortHelper.GetAssort (what a profile sees), RagfairController.GetOffers (the same trader offers on the
// flea market) and TraderAssortHelper.ResetExpiredTrader (the real restock).
// The methods aren't virtual, hence Harmony.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 2)]
public class TraderOverstock(TraderHelper traderHelper, ProfileHelper profileHelper, PaymentHelper paymentHelper, ISptLogger<TraderOverstock> logger, ModHelper modHelper) : IOnLoad
{
    internal static TraderOverstock? Instance;

    // one offer's rounds for one profile
    private sealed class Round
    {
        public int Count;
        public double Multiplier = 1;
    }

    private readonly object _sync = new();
    // trader -> assort id -> stock at the start of the restock cycle (only used to refill the few offers with finite stock)
    private readonly Dictionary<MongoId, Dictionary<MongoId, int>> _stock = new();
    // (profile, trader) -> assort id -> rounds and price multiplier. Prices are never written to the shared trader data:
    // they are applied to the assort sent to that profile only (GetAssort), so other profiles keep the normal price.
    private readonly Dictionary<(MongoId Session, MongoId Trader), Dictionary<MongoId, Round>> _rounds = new();
    private TraderOverstockConfig _cfg = new();
    private HashSet<MongoId> _excluded = [];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _cfg = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").TraderOverstock ?? new TraderOverstockConfig();
        if (!_cfg.Enabled)
        {
            return Task.CompletedTask;
        }

        _excluded = _cfg.ExcludedTraders.Select(id => new MongoId(id)).ToHashSet();
        Instance = this;

        var harmony = new Harmony("com.rpan87.customrptweaks.overstock");
        harmony.Patch(
            AccessTools.Method(typeof(TradeHelper), nameof(TradeHelper.BuyItem)),
            prefix: new HarmonyMethod(typeof(OverstockPatches), nameof(OverstockPatches.BuyPrefix)),
            postfix: new HarmonyMethod(typeof(OverstockPatches), nameof(OverstockPatches.BuyPostfix)));
        harmony.Patch(
            AccessTools.Method(typeof(TraderAssortHelper), nameof(TraderAssortHelper.GetAssort)),
            postfix: new HarmonyMethod(typeof(OverstockPatches), nameof(OverstockPatches.GetAssortPostfix)));
        harmony.Patch(
            AccessTools.Method(typeof(RagfairController), nameof(RagfairController.GetOffers)),
            postfix: new HarmonyMethod(typeof(OverstockPatches), nameof(OverstockPatches.GetOffersPostfix)));
        harmony.Patch(
            AccessTools.Method(typeof(TraderAssortHelper), nameof(TraderAssortHelper.ResetExpiredTrader)),
            postfix: new HarmonyMethod(typeof(OverstockPatches), nameof(OverstockPatches.ResetPostfix)));

        logger.Info($"[CustomRpTweaks] Trader overstock on: {_cfg.MaxRounds} extra rounds, price +{_cfg.MinIncreasePercent}-{_cfg.MaxIncreasePercent}% each");
        return Task.CompletedTask;
    }

    // Buying a trader's offer on the flea market goes through BuyItem too, as "buy_from_ragfair_trader" (ItemId = the assort id)
    private bool Applies(ProcessBuyTradeRequestData request) =>
        (request.Type == ItemEventActions.BUY_FROM_TRADER || request.Type == "buy_from_ragfair_trader")
        && request.TransactionId != Traders.FENCE && !_excluded.Contains(request.TransactionId);

    // Before a purchase: remember the stock of this trader's offers if the restock cycle just started
    internal void BeforeBuy(ProcessBuyTradeRequestData request)
    {
        if (!Applies(request))
        {
            return;
        }

        lock (_sync)
        {
            if (_stock.ContainsKey(request.TransactionId))
            {
                return;
            }

            var assort = traderHelper.GetTraderAssortsByTraderId(request.TransactionId);
            if (assort?.Items is null)
            {
                return;
            }

            _stock[request.TransactionId] = assort.Items
                .Where(i => i.SlotId == "hideout" && i.Upd is not null)
                .ToDictionary(i => i.Id, i => (int)(i.Upd!.StackObjectsCount ?? 1));
        }
    }

    // After a successful purchase: if the offer's limit is used up (or it sold out), start the next round for this profile
    internal void AfterBuy(PmcData pmcData, ProcessBuyTradeRequestData request, MongoId sessionId, ItemEventRouterResponse output)
    {
        if (!Applies(request) || output.Warnings is { Count: > 0 })
        {
            return;
        }

        var traderId = request.TransactionId;
        var item = traderHelper.GetTraderAssortsByTraderId(traderId)?.Items?.FirstOrDefault(i => i.Id == request.ItemId);
        if (item?.Upd is null)
        {
            return;
        }

        lock (_sync)
        {
            var unlimited = item.Upd.UnlimitedCount == true;
            var soldOut = !unlimited && item.Upd.StackObjectsCount <= 0;

            TraderPurchaseData? purchase = null;
            var limitUsed = false;
            if (item.Upd.BuyRestrictionMax is { } max)
            {
                purchase = profileHelper.GetFullProfile(sessionId)?.TraderPurchases?.GetValueOrDefault(traderId)?.GetValueOrDefault(item.Id);
                var limit = traderHelper.GetAccountTypeAdjustedTraderPurchaseLimit(max, pmcData.Info?.GameVersion ?? "");
                limitUsed = purchase?.PurchaseCount >= limit;
            }

            if (!soldOut && !limitUsed)
            {
                return;
            }

            if (!StartRound(sessionId, traderId, item, purchase, soldOut ? "sold out" : "limit used"))
            {
                return;
            }

            // finite stock is shared by every profile, there is only one number to refill
            if (soldOut && _stock.TryGetValue(traderId, out var stock) && stock.TryGetValue(item.Id, out var full))
            {
                item.Upd.StackObjectsCount = full;
            }
        }
    }

    // Caller holds _sync. Next round of one offer for one profile: higher price, purchase counter back to 0.
    // False when the offer already used all its rounds.
    private bool StartRound(MongoId sessionId, MongoId traderId, Item item, TraderPurchaseData? purchase, string reason)
    {
        var key = (sessionId, traderId);
        if (!_rounds.TryGetValue(key, out var offers))
        {
            _rounds[key] = offers = new Dictionary<MongoId, Round>();
        }

        if (!offers.TryGetValue(item.Id, out var round))
        {
            offers[item.Id] = round = new Round();
        }

        if (_cfg.MaxRounds > 0 && round.Count >= _cfg.MaxRounds)
        {
            return false;
        }

        var low = Math.Min(_cfg.MinIncreasePercent, _cfg.MaxIncreasePercent);
        var high = Math.Max(_cfg.MinIncreasePercent, _cfg.MaxIncreasePercent);
        var increase = low + Random.Shared.NextDouble() * (high - low);

        round.Count++;
        round.Multiplier *= 1 + increase / 100;
        if (purchase is not null)
        {
            purchase.PurchaseCount = 0;
        }

        logger.Info($"[CustomRpTweaks] Overstock: profile {sessionId} trader {traderId} item {item.Template} {reason}, round {round.Count}, +{increase:0.#}% (total x{round.Multiplier:0.00})");
        return true;
    }

    // The assort a profile is shown: apply that profile's price multipliers to the (cloned) result
    internal void OnGetAssort(MongoId sessionId, MongoId traderId, TraderAssort? assort)
    {
        if (assort?.BarterScheme is null)
        {
            return;
        }

        lock (_sync)
        {
            RollOverUsedLimits(sessionId, traderId, assort);

            if (!_rounds.TryGetValue((sessionId, traderId), out var offers))
            {
                return;
            }

            foreach (var (assortId, round) in offers.Where(o => o.Value.Multiplier != 1))
            {
                if (!assort.BarterScheme.TryGetValue(assortId, out var options))
                {
                    continue;
                }

                foreach (var scheme in options.SelectMany(option => option))
                {
                    scheme.Count = Scale(scheme.Template, scheme.Count ?? 0, round.Multiplier);
                }
            }
        }
    }

    // Caller holds _sync. A limit can already be used up without a purchase to trigger a round: the counter lives in the
    // profile and survives a server restart, the rounds don't. Start the round when the offers are loaded instead.
    private void RollOverUsedLimits(MongoId sessionId, MongoId traderId, TraderAssort assort)
    {
        if (traderId == Traders.FENCE || _excluded.Contains(traderId) || assort.Items is null)
        {
            return;
        }

        var profile = profileHelper.GetFullProfile(sessionId);
        var purchases = profile?.TraderPurchases?.GetValueOrDefault(traderId);
        if (purchases is null || purchases.Count == 0)
        {
            return;
        }

        var gameVersion = profile!.CharacterData?.PmcData?.Info?.GameVersion ?? "";
        foreach (var item in assort.Items.Where(i => i.SlotId == "hideout" && i.Upd?.BuyRestrictionMax is not null))
        {
            var purchase = purchases.GetValueOrDefault(item.Id);
            var limit = traderHelper.GetAccountTypeAdjustedTraderPurchaseLimit(item.Upd!.BuyRestrictionMax!.Value, gameVersion);
            if (purchase?.PurchaseCount >= limit && StartRound(sessionId, traderId, item, purchase, "limit already used"))
            {
                // the assort was built before the counter was reset, so fix what this response shows
                item.Upd.BuyRestrictionCurrent = 0;
            }
        }
    }

    // roubles in whole units, dollars and euros keep cents (the trader data has prices like 1.07), barter items are
    // rounded up to whole items (and at least 1)
    private double Scale(MongoId template, double count, double multiplier)
    {
        var price = count * multiplier;
        if (!paymentHelper.IsMoneyTpl(template))
        {
            return Math.Max(1, Math.Ceiling(price));
        }

        return template == Money.ROUBLES ? Math.Round(price) : Math.Round(price, 2);
    }

    // Flea market: the trader offers in the search results are pre-built at the base price, so this profile's multiplier is
    // applied to copies of them (the shared offers stay untouched)
    internal void OnGetOffers(MongoId sessionId, GetOffersResult? result)
    {
        if (result?.Offers is null)
        {
            return;
        }

        lock (_sync)
        {
            for (var i = 0; i < result.Offers.Count; i++)
            {
                var offer = result.Offers[i];
                if (!offer.IsTraderOffer() || offer.Requirements is null || offer.Items is not { Count: > 0 })
                {
                    continue;
                }

                if (!_rounds.TryGetValue((sessionId, offer.User.Id), out var offers)
                    || !offers.TryGetValue(offer.Items[0].Id, out var round)
                    || round.Multiplier == 1)
                {
                    continue;
                }

                result.Offers[i] = offer with
                {
                    Requirements = offer.Requirements.Select(r => r with { Count = Scale(r.TemplateId, r.Count ?? 0, round.Multiplier) }).ToList(),
                    // the client reads these two as whole numbers
                    RequirementsCost = offer.RequirementsCost is { } cost ? Math.Round(cost * round.Multiplier) : null,
                    SummaryCost = offer.SummaryCost is { } summary ? Math.Round(summary * round.Multiplier) : null,
                };
            }
        }
    }

    // What the client plugin asks for (/customrptweaks/overstock/prices): for this profile, the price multiplier of every offer
    // that is above its base price, so the client can show the base price next to the new one. "enabled" false (feature or
    // indicator off in the config) tells the client to do nothing.
    internal string GetClientTable(MongoId sessionId)
    {
        var enabled = _cfg.Enabled && _cfg.ClientIndicator;
        var offers = new Dictionary<string, Dictionary<string, double>>();
        if (enabled)
        {
            lock (_sync)
            {
                foreach (var ((session, trader), rounds) in _rounds.Where(r => r.Key.Session == sessionId))
                {
                    var raised = rounds.Where(r => r.Value.Multiplier > 1).ToDictionary(r => r.Key.ToString(), r => Math.Round(r.Value.Multiplier, 4));
                    if (raised.Count > 0)
                    {
                        offers[trader.ToString()] = raised;
                    }
                }
            }
        }

        return JsonSerializer.Serialize(new { enabled, offers });
    }

    // Real restock: every profile's rounds and prices for that trader go back to normal
    internal void AfterReset(Trader trader)
    {
        lock (_sync)
        {
            _stock.Remove(trader.Base.Id);
            var cleared = _rounds.Keys.Where(k => k.Trader == trader.Base.Id).ToList();
            foreach (var key in cleared)
            {
                _rounds.Remove(key);
            }

            if (cleared.Count > 0)
            {
                logger.Info($"[CustomRpTweaks] Overstock: trader {trader.Base.Id} restocked, prices back to normal");
            }
        }
    }
}

// The route the client plugin reads. It is always registered; when the feature is off the answer says so.
[Injectable]
public class OverstockRouter : StaticRouter
{
    private static TraderOverstock? _overstock;

    public OverstockRouter(JsonUtil jsonUtil, TraderOverstock overstock) : base(jsonUtil, GetRoutes())
    {
        _overstock = overstock;
    }

    private static List<RouteAction> GetRoutes() =>
    [
        new RouteAction(
            "/customrptweaks/overstock/prices",
            async (url, request, sessionId, _, _) => await Task.FromResult(_overstock!.GetClientTable(sessionId))),
    ];
}

public static class OverstockPatches
{
    public static void BuyPrefix(ProcessBuyTradeRequestData buyRequestData)
    {
        try { TraderOverstock.Instance?.BeforeBuy(buyRequestData); }
        catch (Exception e) { Console.Error.WriteLine($"[CustomRpTweaks] Overstock prefix failed: {e}"); }
    }

    public static void BuyPostfix(PmcData pmcData, ProcessBuyTradeRequestData buyRequestData, MongoId sessionId, ItemEventRouterResponse output)
    {
        try { TraderOverstock.Instance?.AfterBuy(pmcData, buyRequestData, sessionId, output); }
        catch (Exception e) { Console.Error.WriteLine($"[CustomRpTweaks] Overstock postfix failed: {e}"); }
    }

    public static void GetAssortPostfix(MongoId sessionId, MongoId traderId, TraderAssort __result)
    {
        try { TraderOverstock.Instance?.OnGetAssort(sessionId, traderId, __result); }
        catch (Exception e) { Console.Error.WriteLine($"[CustomRpTweaks] Overstock assort failed: {e}"); }
    }

    public static void GetOffersPostfix(MongoId sessionID, GetOffersResult __result)
    {
        try { TraderOverstock.Instance?.OnGetOffers(sessionID, __result); }
        catch (Exception e) { Console.Error.WriteLine($"[CustomRpTweaks] Overstock flea offers failed: {e}"); }
    }

    public static void ResetPostfix(Trader trader)
    {
        try { TraderOverstock.Instance?.AfterReset(trader); }
        catch (Exception e) { Console.Error.WriteLine($"[CustomRpTweaks] Overstock reset failed: {e}"); }
    }
}

public class TraderOverstockConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // how many extra rounds per offer between trader restocks; a round ends when you use up your personal limit on the
    // offer (or it sells out) and the next one costs more (0 = no cap)
    [JsonPropertyName("maxRounds")] public int MaxRounds { get; set; } = 10;
    // each round the price rises by a random percent in this range, on top of the previous rounds
    [JsonPropertyName("minIncreasePercent")] public double MinIncreasePercent { get; set; } = 10;
    [JsonPropertyName("maxIncreasePercent")] public double MaxIncreasePercent { get; set; } = 20;
    // tell the client plugin which offers are above their base price, so it can show the base price next to the new one
    [JsonPropertyName("clientIndicator")] public bool ClientIndicator { get; set; } = true;
    // trader ids left alone (Fence is always left alone)
    [JsonPropertyName("excludedTraders")] public List<string> ExcludedTraders { get; set; } = [];
}
